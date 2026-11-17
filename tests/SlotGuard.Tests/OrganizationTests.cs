using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class OrganizationTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private const string Header = "X-SlotGuard-Organization";
    private const string University = "university";
    private const string Coworking = "coworking";
    private static readonly DateTimeOffset FirstSlot = SlotGuardFixture.Now.AddMinutes(30);

    [Fact]
    public async Task AccessIsScopedForListsResourceIdsAndOwnedRecords()
    {
        using var guest = await app.CreateGuest();
        using var universityAdmin = await app.CreateDemo("university-admin");
        using var coworkingAdmin = await app.CreateDemo("coworking-admin");

        var guestOrganizations = await guest.GetFromJsonAsync<Access[]>("/api/organizations");
        Assert.Equal(new Access(University, "member"), Assert.Single(guestOrganizations!));
        var adminOrganizations = await universityAdmin.GetFromJsonAsync<Access[]>("/api/organizations");
        Assert.Equal([new Access(University, "admin"), new Access(Coworking, "member")], adminOrganizations!);
        using var universityResources = await Send(guest, HttpMethod.Get, "/api/resources", University);
        using var coworkingResources = await Send(coworkingAdmin, HttpMethod.Get, "/api/resources", Coworking);
        Assert.Equal(["microscope", "electronics", "studio"],
            (await universityResources.Content.ReadFromJsonAsync<ResourceResponse[]>())!.Select(resource => resource.Id));
        Assert.Equal(["meeting-room", "podcast-room"],
            (await coworkingResources.Content.ReadFromJsonAsync<ResourceResponse[]>())!.Select(resource => resource.Id));

        await Expect(guest, HttpMethod.Get, "/api/resources", Coworking, HttpStatusCode.NotFound);
        await Expect(guest, HttpMethod.Get, "/api/calendar?resourceId=meeting-room", University, HttpStatusCode.NotFound);
        await Expect(guest, HttpMethod.Post, "/api/bookings", University, HttpStatusCode.NotFound,
            new SlotRequest("meeting-room", FirstSlot));
        await Expect(guest, HttpMethod.Post, "/api/waitlist", University, HttpStatusCode.NotFound,
            new SlotRequest("meeting-room", FirstSlot));
        await Expect(coworkingAdmin, HttpMethod.Post, "/api/maintenance", Coworking, HttpStatusCode.NotFound,
            new MaintenanceRequest("microscope", FirstSlot, "Проверка"));
        await Expect(universityAdmin, HttpMethod.Post, "/api/maintenance", Coworking, HttpStatusCode.Forbidden,
            new MaintenanceRequest("meeting-room", FirstSlot, "Проверка"));

        var slot = new SlotRequest("microscope", FirstSlot);
        using var booked = await Send(universityAdmin, HttpMethod.Post, "/api/bookings", University, slot);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var booking = (await booked.Content.ReadFromJsonAsync<Identifier>())!;
        await Expect(universityAdmin, HttpMethod.Delete, $"/api/bookings/{booking.Id}", Coworking, HttpStatusCode.NotFound);
        await Expect(universityAdmin, HttpMethod.Post, $"/api/bookings/{booking.Id}/confirm", Coworking, HttpStatusCode.NotFound);
        Assert.Equal("mine", (await ReadSlot(universityAdmin, slot, University)).Status);

        var queueSlot = slot with { ResourceId = "electronics" };
        await Expect(guest, HttpMethod.Post, "/api/bookings", University, HttpStatusCode.Created, queueSlot);
        using var waiting = await Send(universityAdmin, HttpMethod.Post, "/api/waitlist", University, queueSlot);
        Assert.Equal(HttpStatusCode.Created, waiting.StatusCode);
        var entry = (await waiting.Content.ReadFromJsonAsync<WaitlistIdentifier>())!;
        await Expect(universityAdmin, HttpMethod.Delete, $"/api/waitlist/{entry.Id}", Coworking, HttpStatusCode.NotFound);
        Assert.Equal(1, (await ReadSlot(universityAdmin, queueSlot, University)).WaitlistPosition);
    }

    [Fact]
    public async Task MaintenanceIsVisibleToMembersAndCanOnlyBeReopenedByAnAdmin()
    {
        using var guest = await app.CreateGuest();
        using var admin = await app.CreateDemo("university-admin");
        using var outsider = await app.CreateDemo("coworking-admin");
        var slot = new SlotRequest("studio", FirstSlot);
        const string reason = "Замена кабеля";
        var block = new MaintenanceRequest(slot.ResourceId, slot.StartsAt, reason);
        await Expect(guest, HttpMethod.Post, "/api/maintenance", University, HttpStatusCode.Forbidden, block);

        using var blocked = await Send(admin, HttpMethod.Post, "/api/maintenance", University, block);
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
        var id = (await blocked.Content.ReadFromJsonAsync<Identifier>())!.Id;
        var visible = await ReadSlot(guest, slot, University);
        Assert.Equal("maintenance", visible.Status);
        Assert.Equal(reason, visible.MaintenanceReason);
        Assert.Null(visible.BookingId);
        Assert.Null(visible.MaintenanceId);
        Assert.Equal(id, (await ReadSlot(admin, slot, University)).MaintenanceId);
        await Expect(guest, HttpMethod.Post, "/api/bookings", University, HttpStatusCode.Conflict, slot);
        await Expect(guest, HttpMethod.Post, "/api/waitlist", University, HttpStatusCode.Conflict, slot);
        await Expect(guest, HttpMethod.Delete, $"/api/maintenance/{id}", University, HttpStatusCode.Forbidden);
        await Expect(outsider, HttpMethod.Delete, $"/api/maintenance/{id}", Coworking, HttpStatusCode.NotFound);
        await Expect(admin, HttpMethod.Delete, $"/api/bookings/{id}", University, HttpStatusCode.NotFound);
        await Expect(admin, HttpMethod.Delete, $"/api/maintenance/{id}", University, HttpStatusCode.NoContent);
        await Expect(guest, HttpMethod.Post, "/api/bookings", University, HttpStatusCode.Created, slot);
        await Expect(admin, HttpMethod.Post, "/api/maintenance", University, HttpStatusCode.Conflict, block);
    }

    [Fact]
    public async Task ConcurrentMaintenanceAndBookingRequestsLeaveExactlyOneActiveRecord()
    {
        using var guest = await app.CreateGuest();
        using var admin = await app.CreateDemo("university-admin");
        var slot = new SlotRequest("studio", FirstSlot.AddHours(2));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bookings = Enumerable.Range(0, 8).Select(_ => Race(guest, "/api/bookings", slot)).ToArray();
        var blocks = Enumerable.Range(0, 4).Select(_ => Race(admin, "/api/maintenance",
            new MaintenanceRequest(slot.ResourceId, slot.StartsAt, "Диагностика"))).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(bookings.Concat(blocks)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, status => status == HttpStatusCode.Created);
        Assert.Equal(11, results.Count(status => status == HttpStatusCode.Conflict));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var active = Assert.Single(await db.Bookings.AsNoTracking().Where(booking => booking.CancelledAt == null
            && booking.ResourceId == slot.ResourceId && booking.StartUtc == slot.StartsAt).ToListAsync());
        Assert.Equal(results.Skip(bookings.Length).Contains(HttpStatusCode.Created), active.MaintenanceReason is not null);

        async Task<HttpStatusCode> Race(HttpClient client, string path, object payload)
        {
            await start.Task;
            using var response = await Send(client, HttpMethod.Post, path, University, payload);
            return response.StatusCode;
        }
    }

    private static async Task<CalendarSlot> ReadSlot(HttpClient client, SlotRequest slot, string organization)
    {
        using var response = await Send(client, HttpMethod.Get, $"/api/calendar?resourceId={slot.ResourceId}", organization);
        response.EnsureSuccessStatusCode();
        var calendar = (await response.Content.ReadFromJsonAsync<Calendar>())!;
        return Assert.Single(calendar.Days.SelectMany(day => day.Slots), item => item.StartsAt == slot.StartsAt);
    }

    private static async Task Expect(HttpClient client, HttpMethod method, string path, string organization,
        HttpStatusCode expected, object? payload = null)
    {
        using var response = await Send(client, method, path, organization, payload);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        string organization, object? payload = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(Header, organization);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private sealed record Access(string Id, string Role);
    private sealed record ResourceResponse(string Id);
    private sealed record Identifier(Guid Id);
    private sealed record WaitlistIdentifier(long Id);
    private sealed record SlotRequest(string ResourceId, DateTimeOffset StartsAt);
    private sealed record MaintenanceRequest(string ResourceId, DateTimeOffset StartsAt, string Reason);
    private sealed record Calendar(Day[] Days);
    private sealed record Day(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId,
        Guid? MaintenanceId, string? MaintenanceReason, int? WaitlistPosition);
}

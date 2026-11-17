using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class WorkspaceTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private const string Header = "X-SlotGuard-Organization";
    private static readonly DateTimeOffset Nine = SlotGuardFixture.Now.AddMinutes(30);

    [Fact]
    public async Task OverviewAndAgendaUseActualScopedRecords()
    {
        using var member = await app.CreateGuest();
        using var another = await app.CreateGuest();
        using var otherOrganization = await app.CreateDemo("coworking-admin");
        otherOrganization.DefaultRequestHeaders.Add(Header, "coworking");
        var start = Nine.AddDays(7);
        var week = DateOnly.FromDateTime(start.UtcDateTime).ToString("yyyy-MM-dd");
        var booking = await CreateId(member, "/api/bookings", new Slot("microscope", start));
        await CreateId(another, "/api/bookings", new Slot("microscope", start.AddHours(2)));
        using var joined = await member.PostAsJsonAsync("/api/waitlist", new Slot("microscope", start.AddHours(2)));
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);

        var overview = (await member.GetFromJsonAsync<Overview>($"/api/workspace/overview?week={week}"))!;
        var microscope = Assert.Single(overview.Resources, item => item.Id == "microscope");
        Assert.Equal(2, microscope.Counts.Booked);
        Assert.Equal(33, microscope.Counts.Free);
        Assert.Equal(1, overview.Waiting);
        var mine = (await member.GetFromJsonAsync<Agenda>("/api/workspace/me"))!;
        Assert.Equal(booking, Assert.Single(mine.Upcoming).Id);
        Assert.Equal(1, Assert.Single(mine.Waiting).Position);
        var other = (await otherOrganization.GetFromJsonAsync<Overview>($"/api/workspace/overview?week={week}"))!;
        Assert.DoesNotContain(other.Resources, item => item.Id == "microscope");
        Assert.All(other.Resources, item => Assert.Equal(0, item.Counts.Booked));
    }

    [Fact]
    public async Task CatalogEnforcesRolesAndArchivesWithoutLosingHistory()
    {
        using var member = await app.CreateGuest();
        using var admin = await app.CreateDemo("university-admin");
        using var outsider = await app.CreateDemo("coworking-admin");
        outsider.DefaultRequestHeaders.Add(Header, "coworking");
        var initial = new ResourceInput("Анализатор", "Лаборатория 7", false);
        using var denied = await member.PostAsJsonAsync("/api/workspace/resources", initial);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var deniedAudit = await member.GetAsync("/api/workspace/activity");
        Assert.Equal(HttpStatusCode.Forbidden, deniedAudit.StatusCode);

        using var created = await admin.PostAsJsonAsync("/api/workspace/resources", initial);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var resource = (await created.Content.ReadFromJsonAsync<ResourceRow>())!;
        var url = $"/api/workspace/resources/{resource.Id}";
        var updated = initial with { Name = "Анализатор спектра" };
        using var foreign = await outsider.PutAsJsonAsync(url, updated);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var edited = await admin.PutAsJsonAsync(url, updated);
        Assert.Equal(updated.Name, (await edited.Content.ReadFromJsonAsync<ResourceRow>())!.Name);

        var booking = await CreateId(member, "/api/bookings", new Slot(resource.Id, Nine));
        using var conflict = await admin.PutAsJsonAsync(url, updated with { IsArchived = true });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var cancelled = await member.DeleteAsync($"/api/bookings/{booking}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var archived = await admin.PutAsJsonAsync(url, updated with { IsArchived = true });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var visible = (await member.GetFromJsonAsync<ResourceRow[]>("/api/resources"))!;
        Assert.DoesNotContain(visible, item => item.Id == resource.Id);
        using var newBooking = await member.PostAsJsonAsync("/api/bookings", new Slot(resource.Id, Nine));
        Assert.Equal(HttpStatusCode.Conflict, newBooking.StatusCode);
        var myRecords = (await member.GetFromJsonAsync<Agenda>("/api/workspace/me"))!;
        Assert.Equal(booking, Assert.Single(myRecords.History).Id);
        using var restored = await admin.PutAsJsonAsync(url, updated);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var otherAudit = (await outsider.GetFromJsonAsync<Change[]>("/api/workspace/activity"))!;
        Assert.DoesNotContain(otherAudit, entry => entry.ResourceName == updated.Name);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var actions = await db.WorkspaceActivities.Where(item => item.ResourceId == resource.Id)
            .OrderBy(item => item.Id).Select(item => item.Action).ToArrayAsync();
        Assert.Equal(["resource.created", "resource.updated", "resource.archived", "resource.restored"], actions);
    }

    [Fact]
    public async Task MaintenanceWindowIsAtomicAndIdentifiesConflicts()
    {
        using var member = await app.CreateGuest();
        using var admin = await app.CreateDemo("university-admin");
        var start = Nine.AddDays(14);
        var day = DateOnly.FromDateTime(start.UtcDateTime);
        var requested = new { resourceId = "studio", date = day, fromHour = 9, throughHour = 13, reason = "Калибровка" };
        var reserved = await CreateId(member, "/api/bookings", new Slot("studio", start.AddHours(2)));
        using var rejected = await admin.PostAsJsonAsync("/api/maintenance/window", requested);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var conflict = (await rejected.Content.ReadFromJsonAsync<Conflict>())!;
        Assert.Equal(start.AddHours(2), Assert.Single(conflict.Conflicts));
        Assert.Equal("available", (await ReadSlot(member, start)).Status);
        Assert.Equal("available", (await ReadSlot(member, start.AddHours(4))).Status);
        using var freed = await member.DeleteAsync($"/api/bookings/{reserved}");
        Assert.Equal(HttpStatusCode.NoContent, freed.StatusCode);
        using var accepted = await admin.PostAsJsonAsync("/api/maintenance/window", requested);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(3, (await accepted.Content.ReadFromJsonAsync<Window>())!.Count);
        foreach (var hour in new[] { 0, 2, 4 })
            Assert.Equal("maintenance", (await ReadSlot(member, start.AddHours(hour))).Status);
    }

    [Fact]
    public async Task BookingAndArchivalCannotBothWin()
    {
        using var member = await app.CreateGuest();
        using var admin = await app.CreateDemo("university-admin");
        var input = new ResourceInput("Осциллограф", "Мастерская", false);
        using var created = await admin.PostAsJsonAsync("/api/workspace/resources", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var resource = (await created.Content.ReadFromJsonAsync<ResourceRow>())!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedule = Race(() => member.PostAsJsonAsync("/api/bookings", new Slot(resource.Id, Nine)));
        var archive = Race(() => admin.PutAsJsonAsync($"/api/workspace/resources/{resource.Id}", input with { IsArchived = true }));
        start.SetResult();
        var statuses = await Task.WhenAll(schedule, archive).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        Assert.True(statuses[0] == HttpStatusCode.Created || statuses[1] == HttpStatusCode.OK);

        async Task<HttpStatusCode> Race(Func<Task<HttpResponseMessage>> action)
        {
            await start.Task;
            using var response = await action();
            return response.StatusCode;
        }
    }

    private static async Task<Guid> CreateId(HttpClient client, string url, object payload)
    {
        using var response = await client.PostAsJsonAsync(url, payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Identifier>())!.Id;
    }

    private static async Task<CalendarSlot> ReadSlot(HttpClient client, DateTimeOffset value)
    {
        var week = DateOnly.FromDateTime(value.UtcDateTime).ToString("yyyy-MM-dd");
        var calendar = (await client.GetFromJsonAsync<Calendar>($"/api/calendar?resourceId=studio&week={week}"))!;
        return Assert.Single(calendar.Days.SelectMany(day => day.Slots), item => item.StartsAt == value);
    }

    private sealed record ResourceInput(string Name, string Location, bool IsArchived);
    private sealed record ResourceRow(string Id, string Name);
    private sealed record Slot(string ResourceId, DateTimeOffset StartsAt);
    private sealed record Identifier(Guid Id);
    private sealed record Conflict(DateTimeOffset[] Conflicts);
    private sealed record Window(int Count);
    private sealed record Change(string ResourceName);
    private sealed record Overview(OverviewResource[] Resources, int Waiting);
    private sealed record OverviewResource(string Id, Counts Counts);
    private sealed record Counts(int Booked, int Free);
    private sealed record Agenda(Identifier[] Upcoming, Identifier[] History, Waiting[] Waiting);
    private sealed record Waiting(int Position);
    private sealed record Calendar(Day[] Days);
    private sealed record Day(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status);
}

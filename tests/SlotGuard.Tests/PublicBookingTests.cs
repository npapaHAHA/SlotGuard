using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class PublicBookingTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateTimeOffset Start = SlotGuardFixture.Now.AddMinutes(30);
    private const string Photo = "https://assets.example.test/room.webp";

    [Fact]
    public async Task LinkIsOptInAndCanBeManagedOnlyByTheOwningOrganizationAdmin()
    {
        using var admin = await app.CreateDemo("coworking-admin");
        admin.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, "coworking");
        using var member = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var resource = await CreateResource(admin, "Светлая переговорная");
        Assert.Null(resource.PublicLinkId);
        using var byResourceId = await guest.GetAsync($"/api/public/resources/{resource.Id}");
        Assert.Equal(HttpStatusCode.NotFound, byResourceId.StatusCode);

        using var unauthorized = await SetLink(guest, resource.Id, true);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var wrongOrganization = await SetLink(member, resource.Id, true);
        Assert.Equal(HttpStatusCode.NotFound, wrongOrganization.StatusCode);
        member.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, "coworking");
        using var memberDenied = await SetLink(member, resource.Id, true);
        Assert.Equal(HttpStatusCode.Forbidden, memberDenied.StatusCode);
        using var wrongPhoto = await SetLink(admin, resource.Id, true, "file:///photo.webp");
        Assert.Equal(HttpStatusCode.BadRequest, wrongPhoto.StatusCode);

        var link = await Publish(admin, resource.Id, Photo);
        using var page = await guest.GetAsync($"/book/{link}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("public-booking.js", await page.Content.ReadAsStringAsync());
        using var available = await guest.GetAsync(Address(link));
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        Assert.True(available.Headers.CacheControl?.NoStore);
        Assert.False(available.Headers.Contains("Set-Cookie"));
        var view = (await available.Content.ReadFromJsonAsync<PublicBookingEndpoints.PublicResource>())!;
        Assert.Equal(resource.Name, view.Name);
        Assert.Equal(resource.Location, view.Location);
        Assert.Equal(resource.Capacity, view.Capacity);
        Assert.Equal(resource.Equipment, view.Equipment);
        Assert.Equal(Photo, view.PhotoUrl);
        Assert.Contains(Start, view.Slots);

        using var disabled = await SetLink(admin, resource.Id, false);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var oldPage = await guest.GetAsync($"/book/{link}");
        Assert.Equal(HttpStatusCode.NotFound, oldPage.StatusCode);
        using var oldRead = await guest.GetAsync(Address(link));
        Assert.Equal(HttpStatusCode.NotFound, oldRead.StatusCode);
        using var oldWrite = await Book(guest, link, Start);
        Assert.Equal(HttpStatusCode.NotFound, oldWrite.StatusCode);

        var rotated = await Publish(admin, resource.Id);
        Assert.NotEqual(link, rotated);
        Assert.Equal(rotated, await Publish(admin, resource.Id));
    }

    [Fact]
    public async Task CompetingGuestsGetOneBookingAndNearestTimesOnTheSameResource()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var first = app.CreateClient();
        using var second = app.CreateClient();
        var resource = await CreateResource(admin, "Лаборатория");
        var link = await Publish(admin, resource.Id);
        var requests = await Task.WhenAll(Book(first, link, Start), Book(second, link, Start));
        using var created = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Created);
        using var conflict = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Conflict);
        var winner = ReferenceEquals(requests[0], created) ? first : second;
        var other = ReferenceEquals(winner, first) ? second : first;
        var booking = (await created.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.Equal("confirmed", booking.Status);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        var alternatives = problem.RootElement.GetProperty("alternatives").Deserialize<DateTimeOffset[]>()!;
        Assert.Equal(6, alternatives.Length);
        Assert.Equal(Start.AddHours(2), alternatives[0]);
        Assert.DoesNotContain(Start, alternatives);
        var refreshed = await other.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(Address(link));
        Assert.DoesNotContain(Start, refreshed!.Slots);
        Assert.All(alternatives, time => Assert.Contains(time, refreshed.Slots));

        using var retry = await Book(winner, link, Start);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(booking.Id, (await retry.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!.Id);
        using var alternative = await Book(other, link, alternatives[0]);
        Assert.Equal(HttpStatusCode.Created, alternative.StatusCode);
    }

    [Fact]
    public async Task PublicApprovalUsesExistingQueueAndReceiptIsVisibleOnlyToItsOwner()
    {
        using var admin = await app.CreateDemo("coworking-admin");
        admin.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, "coworking");
        using var guest = app.CreateClient();
        using var stranger = await app.CreateGuest();
        var resource = await CreateResource(admin, "Зал с администратором", true);
        var sibling = await CreateResource(admin, "Другой зал");
        var link = await Publish(admin, resource.Id);
        var siblingLink = await Publish(admin, sibling.Id);
        using var response = await Book(guest, link, Start);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("SlotGuard.Public=", cookie);
        Assert.Contains("path=/api/public", cookie);
        var booking = (await response.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.Equal("pending", booking.Status);
        var address = $"{Address(link)}/bookings/{booking.Id}";
        using var foreignReceipt = await stranger.GetAsync(address);
        Assert.Equal(HttpStatusCode.NotFound, foreignReceipt.StatusCode);
        using var wrongResource = await guest.GetAsync($"{Address(siblingLink)}/bookings/{booking.Id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongResource.StatusCode);
        using var internalCatalog = await guest.GetAsync("/api/workspace/resources");
        Assert.Equal(HttpStatusCode.Unauthorized, internalCatalog.StatusCode);

        var approvals = await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals");
        Assert.Equal(resource.Name, Assert.Single(approvals!, request => request.Id == booking.Id).ResourceName);
        using var approve = await admin.PostAsJsonAsync($"/api/approvals/{booking.Id}/decision",
            new { status = "approved", reason = "Доступ согласован" });
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        var receipt = await guest.GetFromJsonAsync<PublicBookingEndpoints.Receipt>(address);
        Assert.Equal("approved", receipt!.Status);
        Assert.Equal("Доступ согласован", receipt.Reason);
    }

    [Fact]
    public async Task ArchivingRevokesLinkAndRestoringDoesNotRepublish()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var resource = await CreateResource(admin, "Архивируемый зал");
        var link = await Publish(admin, resource.Id);

        using var archived = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource.Id}",
            new { resource.Name, resource.Location, isArchived = true });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        using var inaccessible = await guest.GetAsync(Address(link));
        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        using var publishArchived = await SetLink(admin, resource.Id, true);
        Assert.Equal(HttpStatusCode.Conflict, publishArchived.StatusCode);

        using var restored = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource.Id}",
            new { resource.Name, resource.Location, isArchived = false });
        Assert.Null((await restored.Content.ReadFromJsonAsync<Resource>())!.PublicLinkId);
        using var stale = await guest.GetAsync(Address(link));
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
    }

    private static string Address(Guid link) => $"/api/public/resources/{link}";

    private static Task<HttpResponseMessage> SetLink(HttpClient admin, string resourceId, bool enabled,
        string? photoUrl = null) =>
        admin.PutAsJsonAsync($"/api/workspace/resources/{resourceId}/public-link", new { enabled, photoUrl });

    private static async Task<Guid> Publish(HttpClient admin, string resourceId, string? photoUrl = null)
    {
        using var response = await SetLink(admin, resourceId, true, photoUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublishedLink>())!.PublicLinkId;
    }

    private static Task<HttpResponseMessage> Book(HttpClient guest, Guid link, DateTimeOffset startsAt) =>
        guest.PostAsJsonAsync($"{Address(link)}/bookings", new { startsAt });

    private static async Task<Resource> CreateResource(HttpClient admin, string name, bool requiresApproval = false)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources", new
        {
            name, location = "Корпус Б · этаж 2", requiresApproval,
            details = new { type = "room", capacity = 8, equipment = new[] { "экран", "доска" } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Resource>())!;
    }

    private sealed record PublishedLink(Guid PublicLinkId);
}

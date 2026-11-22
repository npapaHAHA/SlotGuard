using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class PrivateLinkFixture : SlotGuardFixture
{
    internal AccountFixture.TestMail Mail { get; } = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IWorkspaceMail>(Mail);
}

public sealed class PrivateLinkTests(PrivateLinkFixture app) : IClassFixture<PrivateLinkFixture>
{
    private static readonly DateTimeOffset Start = SlotGuardFixture.Now.AddHours(2.5);

    [Fact]
    public async Task DifferentSlotsRaceForOneUseAndOnlyTheOwnerCanManageTheReceipt()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var first = app.CreateClient();
        using var second = app.CreateClient();
        var resource = await Create(admin, "Одно приглашение");
        var link = await Invite(admin, resource.Id, 1);
        var path = GuestPath(link);
        await first.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(path);
        var replies = await Task.WhenAll(Book(first, path, Start), Book(second, path, Start.AddHours(2)))
            .WaitAsync(TimeSpan.FromSeconds(35));
        using var winner = Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Created);
        using var loser = Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Gone);
        var client = ReferenceEquals(replies[0], winner) ? first : second;
        var stranger = ReferenceEquals(replies[0], winner) ? second : first;
        var receipt = (await winner.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        var closed = await client.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(path);
        Assert.Equal(receipt.Id, closed!.PreviousBookingId);
        Assert.Empty(closed.Slots);
        using var stalePage = await stranger.GetAsync(path);
        Assert.Equal(HttpStatusCode.Gone, stalePage.StatusCode);
        using var replay = await Book(client, path, receipt.StartsAt);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var forbidden = await stranger.GetAsync($"{path}/bookings/{receipt.Id}");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        var otherLink = await Invite(admin, resource.Id, 2);
        using var wrongLink = await client.GetAsync($"{GuestPath(otherLink)}/bookings/{receipt.Id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongLink.StatusCode);

        using var revoked = await admin.DeleteAsync($"{ManagePath(resource.Id)}/{link.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var moved = await client.PostAsJsonAsync($"{path}/bookings/{receipt.Id}/reschedule", new { startsAt = Start.AddHours(4) });
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        var successor = (await moved.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        using var cancelled = await client.DeleteAsync($"{path}/bookings/{successor.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var saved = await Lookup(admin, resource.Id, link.Id);
        Assert.Equal(1, saved.UsedCount);
        Assert.Equal("revoked", saved.Status);
    }

    [Fact]
    public async Task DomainNeedsMailboxProofScopedToThisLinkAndUsesTheProvenContact()
    {
        const string email = "organizer@example.test";
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        using var browser = app.CreateClient();
        var resource = await Create(admin, "Приглашение для команды");
        var link = await Invite(admin, resource.Id, 2, "EXAMPLE.test");
        Assert.Equal("example.test", link.EmailDomain);
        var path = GuestPath(link);
        using var locked = await guest.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.DoesNotContain(resource.Name, await locked.Content.ReadAsStringAsync());
        using var claimed = await Book(guest, path, Start, email);
        Assert.Equal(HttpStatusCode.Forbidden, claimed.StatusCode);
        using var wrong = await guest.PostAsJsonAsync(path + "/verify", new { email = "guest@sub.example.test" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        using var sent = await guest.PostAsJsonAsync(path + "/verify", new { email });
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        var ticket = app.Mail.Latest(email, PrivateLinkVerification.MailAction);
        var another = await Invite(admin, resource.Id, 1, "example.test");
        using var misplaced = await browser.PostAsJsonAsync(GuestPath(another) + "/verify/complete", new { ticket.Token });
        Assert.Equal(HttpStatusCode.Forbidden, misplaced.StatusCode);
        using var proof = await browser.PostAsJsonAsync(path + "/verify/complete", new { ticket.Token });
        Assert.Equal(HttpStatusCode.NoContent, proof.StatusCode);
        Assert.Contains(proof.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("SlotGuard.PrivateAccess=") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var unlocked = await browser.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(path);
        Assert.Equal(email, unlocked!.VerifiedEmail);
        using var deniedOther = await browser.GetAsync(GuestPath(another));
        Assert.Equal(HttpStatusCode.Forbidden, deniedOther.StatusCode);

        using var booked = await Book(browser, path, Start, "spoof@outside.test");
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var booking = (await booked.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        Assert.Equal(email, (await db.Bookings.FindAsync(booking.Id))!.ContactEmail);
        app.SetTime(SlotGuardFixture.Now.AddMinutes(16));
        try
        {
            using var expiredProof = await guest.PostAsJsonAsync(path + "/verify/complete", new { ticket.Token });
            Assert.Equal(HttpStatusCode.Forbidden, expiredProof.StatusCode);
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    [Fact]
    public async Task ExpiryAndArchiveStopNewBookingsAndAdministratorsAreScoped()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var foreign = await app.CreateDemo("coworking-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var resource = await Create(admin, "Ссылка с дедлайном");
        using var denied = await member.GetAsync(ManagePath(resource.Id));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var scoped = await foreign.PostAsJsonAsync(ManagePath(resource.Id), new
        {
            maxUses = 2, expiresAt = SlotGuardFixture.Now.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.NotFound, scoped.StatusCode);
        var soon = await Invite(admin, resource.Id, 2, expiresAt: SlotGuardFixture.Now.AddSeconds(10));
        var available = await guest.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(GuestPath(soon));
        Assert.Equal(2, available!.RemainingUses);
        app.SetTime(SlotGuardFixture.Now.AddSeconds(10));
        try
        {
            using var expired = await Book(guest, GuestPath(soon), Start);
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            Assert.Equal(0, (await Lookup(admin, resource.Id, soon.Id)).UsedCount);
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
        var active = await Invite(admin, resource.Id, 2);
        var address = $"/api/workspace/resources/{resource.Id}";
        using var archived = await admin.PutAsJsonAsync(address, new { resource.Name, resource.Location, isArchived = true });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        using var unavailable = await guest.GetAsync(GuestPath(active));
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        using var restored = await admin.PutAsJsonAsync(address, new { resource.Name, resource.Location });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var stillClosed = await guest.GetAsync(GuestPath(active));
        Assert.Equal(HttpStatusCode.Gone, stillClosed.StatusCode);
    }

    [Fact]
    public async Task CountsEachSeriesOccurrenceOnlyIfTheWholeSeriesCommits()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var resource = await Create(admin, "Серийное приглашение");
        var link = await Invite(admin, resource.Id, 2);
        var path = GuestPath(link) + "/series";
        var request = new WeeklySeriesInput(Start, 3, SeriesId: Guid.NewGuid());
        using var excessive = await guest.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excessive.StatusCode);

        using var occupied = await member.PostAsJsonAsync("/api/bookings", new { resourceId = resource.Id, startsAt = Start.AddDays(7) });
        Assert.Equal(HttpStatusCode.Created, occupied.StatusCode);
        var blocker = (await occupied.Content.ReadFromJsonAsync<CreatedBooking>())!;
        request = request with { Count = 2 };
        using var conflict = await guest.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(0, (await Lookup(admin, resource.Id, link.Id)).UsedCount);
        using var released = await member.DeleteAsync($"/api/bookings/{blocker.Id}");
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        using var success = await guest.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);
        var series = (await success.Content.ReadFromJsonAsync<CreatedSeries>())!;
        Assert.Equal(2, series.Bookings.Length);
        using var replay = await guest.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var state = await Lookup(admin, resource.Id, link.Id);
        Assert.Equal(2, state.UsedCount);
        Assert.Equal("used", state.Status);
    }

    private static string ManagePath(string resource) => $"/api/workspace/resources/{resource}/private-links";
    private static string GuestPath(PrivateLinkAdmin.View link) => $"/api/public/resources/{link.Id}";

    private static async Task<PrivateLinkAdmin.View> Invite(HttpClient admin, string resource, int max, string? domain = null,
        DateTimeOffset? expiresAt = null)
    {
        using var response = await admin.PostAsJsonAsync(ManagePath(resource), new
        {
            maxUses = max, emailDomain = domain, expiresAt = expiresAt ?? SlotGuardFixture.Now.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PrivateLinkAdmin.View>())!;
    }

    private static async Task<PrivateLinkAdmin.View> Lookup(HttpClient admin, string resource, Guid id) =>
        Assert.Single((await admin.GetFromJsonAsync<PrivateLinkAdmin.View[]>(ManagePath(resource)))!, link => link.Id == id);

    private static Task<HttpResponseMessage> Book(HttpClient guest, string resource, DateTimeOffset start, string? email = null) =>
        guest.PostAsJsonAsync(resource + "/bookings", new { startsAt = start, email });

    private static async Task<Resource> Create(HttpClient admin, string name)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources", new { name, location = "Корпус Б" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Resource>())!;
    }
}

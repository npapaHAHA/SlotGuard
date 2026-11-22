using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class RescheduleTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateOnly Day = new(2026, 5, 12);

    [Fact]
    public async Task ASlotTakenAfterOpeningThePickerLeavesTheOriginalAndItsQueueIntact()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var contender = await app.CreateGuest();
        using var waiting = await app.CreateGuest();
        var resource = await CreateResource(admin);
        var original = await Book(owner, resource.Id, At(9));

        using var joined = await waiting.PostAsJsonAsync("/api/waitlist", new { resourceId = resource.Id, startsAt = At(9) });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var choices = (await owner.GetFromJsonAsync<RescheduleView>(Address(original.Id)))!;
        Assert.Contains(At(11), choices.Starts);
        await Book(contender, resource.Id, At(11));

        using var conflict = await Move(owner, original.Id, At(11));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var agenda = await AgendaOf(owner);
        Assert.Equal(original.Id, Assert.Single(agenda.Upcoming).Id);
        Assert.Equal(At(9), agenda.Upcoming[0].StartsAt);
        Assert.Empty(agenda.History);
        Assert.Single((await AgendaOf(waiting)).Waiting);
        Assert.Empty((await AgendaOf(waiting)).Upcoming);

        var refreshed = (await owner.GetFromJsonAsync<RescheduleView>(Address(original.Id)))!;
        Assert.DoesNotContain(At(11), refreshed.Starts);
        Assert.Contains(At(13), refreshed.Starts);
    }

    [Fact]
    public async Task OverlappingTheOldSlotPreservesDurationReopensApprovalAndOffersOnlyTheReleasedPart()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var first = await app.CreateGuest();
        using var second = await app.CreateGuest();
        var resource = await CreateResource(admin, requiresApproval: true, flexible: true);
        var original = await Book(owner, resource.Id, At(9), 60);
        using var approved = await admin.PostAsJsonAsync($"/api/approvals/{original.Id}/decision",
            new { status = "approved", reason = "Время согласовано" });
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        using var firstJoin = await first.PostAsJsonAsync("/api/waitlist",
            new { resourceId = resource.Id, startsAt = At(9), durationMinutes = 30 });
        using var secondJoin = await second.PostAsJsonAsync("/api/waitlist",
            new { resourceId = resource.Id, startsAt = At(9, 15), durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.Created, firstJoin.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondJoin.StatusCode);

        var options = (await owner.GetFromJsonAsync<RescheduleView>(Address(original.Id)))!;
        Assert.True(options.RequiresApproval);
        Assert.Contains(At(9, 30), options.Starts);
        using var moved = await Move(owner, original.Id, At(9, 30));
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        var replacement = (await moved.Content.ReadFromJsonAsync<CreatedBooking>())!;
        Assert.Equal(At(9, 30), replacement.StartsAt);
        Assert.Equal(At(10, 30), replacement.EndsAt);
        Assert.Equal("pending", replacement.ApprovalStatus);
        var agenda = await AgendaOf(owner);
        Assert.Equal(replacement.Id, Assert.Single(agenda.Upcoming).Id);
        var previous = Assert.Single(agenda.History);
        Assert.Equal(original.Id, previous.Id);
        Assert.True(previous.IsRescheduled);

        var offer = Assert.Single((await AgendaOf(first)).Upcoming);
        Assert.Equal(At(9), offer.StartsAt);
        Assert.Equal(At(9, 30), offer.EndsAt);
        Assert.Equal(SlotGuardFixture.Now.AddMinutes(10), offer.HoldExpiresAt);
        Assert.Empty((await AgendaOf(second)).Upcoming);
        Assert.Single((await AgendaOf(second)).Waiting);
        var approvals = (await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals"))!;
        Assert.Contains(approvals, item => item.Id == replacement.Id);
        Assert.DoesNotContain(approvals, item => item.Id == original.Id);
    }

    [Fact]
    public async Task AConcurrentBookingAndMoveHaveOneWinnerAndKeepTheOwnerBooked()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var contender = await app.CreateGuest();
        var resource = await CreateResource(admin, flexible: true);
        var original = await Book(owner, resource.Id, At(9), 60);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = Compete(() => Move(owner, original.Id, At(11)));
        var book = Compete(() => contender.PostAsJsonAsync("/api/bookings",
            new { resourceId = resource.Id, startsAt = At(11, 15), durationMinutes = 30 }));
        gate.SetResult();
        var statuses = await Task.WhenAll(move, book).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(statuses, result => result == HttpStatusCode.Created);
        Assert.Single(statuses, result => result == HttpStatusCode.Conflict);
        var ownerBooking = Assert.Single((await AgendaOf(owner)).Upcoming);
        Assert.Equal(statuses[0] == HttpStatusCode.Created ? At(11) : At(9), ownerBooking.StartsAt);

        async Task<HttpStatusCode> Compete(Func<Task<HttpResponseMessage>> send)
        {
            await gate.Task;
            using var response = await send();
            return response.StatusCode;
        }
    }

    [Fact]
    public async Task ADatabaseConflictDuringReplacementRestoresTheOldBooking()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        var resource = await CreateResource(admin);
        var original = await Book(owner, resource.Id, At(9));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_reschedule_for_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RescheduledFromId" IS NOT NULL THEN
                    RAISE EXCEPTION 'Test conflict on replacement' USING ERRCODE = '23P01',
                        CONSTRAINT = 'ex_bookings_active_window';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER reject_reschedule_for_test BEFORE INSERT ON "Bookings"
                FOR EACH ROW EXECUTE FUNCTION reject_reschedule_for_test();
            """);
        try
        {
            using var conflict = await Move(owner, original.Id, At(11));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_reschedule_for_test ON "Bookings";
                DROP FUNCTION reject_reschedule_for_test();
                """);
        }

        var persisted = await db.Bookings.AsNoTracking().Where(item => item.ResourceId == resource.Id).ToArrayAsync();
        var kept = Assert.Single(persisted);
        Assert.Equal(original.Id, kept.Id);
        Assert.Equal(At(9), kept.StartUtc);
        Assert.Null(kept.CancelledAt);
        Assert.False(await db.WorkspaceActivities.AnyAsync(item => item.ResourceId == resource.Id && item.Action == "booking.rescheduled"));
    }

    [Fact]
    public async Task GuestCanMoveOnlyTheirOwnBookingOnTheSameLinkAndReplayingTheMoveIsSafe()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = app.CreateClient();
        using var otherGuest = app.CreateClient();
        var resource = await CreateResource(admin);
        var otherResource = await CreateResource(admin);
        var link = await Publish(admin, resource.Id);
        var otherLink = await Publish(admin, otherResource.Id);
        var root = $"/api/public/resources/{link}/bookings";
        using var booked = await owner.PostAsJsonAsync(root, new { startsAt = At(9) });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var original = (await booked.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.True(original.CanReschedule);
        var address = $"{root}/{original.Id}/reschedule";
        using var noSession = await otherGuest.PostAsJsonAsync(address, new { startsAt = At(11) });
        Assert.Equal(HttpStatusCode.NotFound, noSession.StatusCode);
        using var wrongLink = await owner.GetAsync($"/api/public/resources/{otherLink}/bookings/{original.Id}/reschedule");
        Assert.Equal(HttpStatusCode.NotFound, wrongLink.StatusCode);

        using var moved = await owner.PostAsJsonAsync(address, new { startsAt = At(11) });
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        var replacement = (await moved.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.Equal(At(11), replacement.StartsAt);
        using var retry = await owner.PostAsJsonAsync(address, new { startsAt = At(11) });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(replacement.Id, (await retry.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!.Id);
        using var competingMove = await owner.PostAsJsonAsync(address, new { startsAt = At(13) });
        Assert.Equal(HttpStatusCode.Conflict, competingMove.StatusCode);
        var previous = (await owner.GetFromJsonAsync<PublicBookingEndpoints.Receipt>($"{root}/{original.Id}"))!;
        Assert.Equal("rescheduled", previous.Status);
        Assert.False(previous.CanReschedule);
        Assert.Equal(replacement.Id, previous.RescheduledToId);
    }

    private static DateTimeOffset At(int hour, int minute = 0) => Schedule.AtTime(Day, new TimeOnly(hour, minute));
    private static string Address(Guid id) => $"/api/bookings/{id}/reschedule";
    private static Task<HttpResponseMessage> Move(HttpClient owner, Guid id, DateTimeOffset startsAt) =>
        owner.PostAsJsonAsync(Address(id), new { startsAt });

    private static async Task<CreatedBooking> Book(HttpClient client, string resourceId, DateTimeOffset startsAt, int durationMinutes = 60)
    {
        using var response = await client.PostAsJsonAsync("/api/bookings", new { resourceId, startsAt, durationMinutes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
    }

    private static async Task<Resource> CreateResource(HttpClient admin, bool requiresApproval = false, bool flexible = false)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name = "Переговорная для переноса", location = "Тестовый зал", requiresApproval });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var resource = (await response.Content.ReadFromJsonAsync<Resource>())!;
        if (flexible)
        {
            var closed = new DayHours([], []);
            var open = new DayHours([new(540, 1020)], []);
            var schedule = new ResourceSchedule(Enumerable.Repeat(closed, 7).ToArray(), [new(Day, open)], Booking: new BookingRules(true));
            using var configured = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource.Id}/schedule", schedule);
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        return resource;
    }

    private static async Task<Guid> Publish(HttpClient admin, string id)
    {
        using var response = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Link>())!.PublicLinkId;
    }

    private static async Task<Agenda> AgendaOf(HttpClient client) =>
        (await client.GetFromJsonAsync<Agenda>("/api/workspace/me"))!;

    private sealed record Link(Guid PublicLinkId);
    private sealed record Agenda(AgendaItem[] Upcoming, AgendaItem[] History, WaitingItem[] Waiting);
    private sealed record AgendaItem(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsRescheduled,
        DateTimeOffset? HoldExpiresAt);
    private sealed record WaitingItem(long Id);
}

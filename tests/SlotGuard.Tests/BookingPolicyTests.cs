using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class BookingPolicyTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private const HttpStatusCode PolicyBlocked = HttpStatusCode.UnprocessableEntity;
    private static readonly DateOnly Day = new(2026, 5, 11);

    [Fact]
    public async Task LeadTimeAndRollingWindowAgreeAcrossCalendarSearchGuestAndWrite()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var id = await Create(admin, "policy-window", new(MinimumNoticeMinutes: 60, BookingWindowDays: 1));
        var link = await Publish(admin, id);
        var calendar = (await member.GetFromJsonAsync<CalendarView>($"/api/calendar?resourceId={id}"))!;
        var slots = calendar.Days.SelectMany(day => day.Slots).ToArray();
        Assert.Equal("restricted", Assert.Single(slots, slot => slot.StartsAt == At(9, 15)).Status);
        Assert.Equal("available", Assert.Single(slots, slot => slot.StartsAt == At(9, 30)).Status);
        Assert.Equal("available", Assert.Single(slots, slot => slot.StartsAt == At(8, 30, offsetDays: 1)).Status);
        Assert.Equal("restricted", Assert.Single(slots, slot => slot.StartsAt == At(8, 45, offsetDays: 1)).Status);

        var read = (await guest.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>($"/api/public/resources/{link}"))!;
        Assert.Equal(At(9, 30), read.Slots.First());
        Assert.Equal(At(8, 30, offsetDays: 1), read.Slots.Last());
        Assert.Equal(Day.AddDays(1), read.LastDay);
        var search = (await member.GetFromJsonAsync<AvailabilitySearch.SearchResult>(
            $"/api/availability?date={Day:yyyy-MM-dd}&time=09:15&equipment=policy-window"))!;
        Assert.Empty(search.Exact);
        Assert.Equal(At(9, 30), search.Alternatives.First().StartsAt);

        using var tooSoon = await Book(member, id, At(9, 15));
        using var tooFar = await Book(member, id, At(8, 45, offsetDays: 1));
        using var exactLead = await Book(member, id, At(9, 30));
        using var exactLimit = await Book(member, id, At(8, 30, offsetDays: 1));
        Assert.Equal(PolicyBlocked, tooSoon.StatusCode);
        Assert.Equal(PolicyBlocked, tooFar.StatusCode);
        Assert.Equal(HttpStatusCode.Created, exactLead.StatusCode);
        Assert.Equal(HttpStatusCode.Created, exactLimit.StatusCode);
    }

    [Fact]
    public async Task ConcurrentBookingsCannotExceedThePerOwnerLimitForOneResource()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var other = await app.CreateGuest();
        var limited = await Create(admin, "policy-quota", new(MaxActiveBookings: 1));
        var secondResource = await Create(admin, "policy-quota-two", new(MaxActiveBookings: 1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new[] { At(9), At(11) }.Select(async start =>
        {
            await gate.Task;
            using var result = await Book(member, limited, start);
            return result.StatusCode;
        }).ToArray();
        gate.SetResult();
        var statuses = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(statuses, result => result == HttpStatusCode.Created);
        Assert.Single(statuses, result => result == PolicyBlocked);

        using var anotherOwner = await Book(other, limited, At(13));
        using var anotherResource = await Book(member, secondResource, At(13));
        Assert.Equal(HttpStatusCode.Created, anotherOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Created, anotherResource.StatusCode);
        var search = (await member.GetFromJsonAsync<AvailabilitySearch.SearchResult>(
            $"/api/availability?date={Day:yyyy-MM-dd}&time=15:00&equipment=policy-quota"))!;
        Assert.Empty(search.Exact);
        Assert.Empty(search.Alternatives);
        Assert.NotNull(search.BookingMessage);

        var item = Assert.Single((await Agenda(member)).Upcoming, booking => booking.ResourceId == limited);
        using var cancellation = await member.DeleteAsync($"/api/bookings/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        using var rebook = await Book(member, limited, At(15));
        Assert.Equal(HttpStatusCode.Created, rebook.StatusCode);
    }

    [Fact]
    public async Task IndependentDeadlinesKeepLateChangesFromMutatingTheOriginalBooking()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var id = await Create(admin, "policy-deadline", new(CancellationNoticeMinutes: 60, RescheduleNoticeMinutes: 120));
        var original = await CreateBooking(member, id, At(11));
        var later = await CreateBooking(member, id, At(13));

        try
        {
            app.SetTime(At(9));
            using var atRescheduleCutoff = await member.GetAsync($"/api/bookings/{original.Id}/reschedule");
            Assert.Equal(HttpStatusCode.OK, atRescheduleCutoff.StatusCode);
            app.SetTime(At(9, 1));
            var changes = Assert.Single((await Agenda(member)).Upcoming, booking => booking.Id == original.Id).Changes!;
            Assert.True(changes.CanCancel);
            Assert.False(changes.CanReschedule);
            Assert.Equal(At(9), changes.RescheduleUntil);
            using var lateReschedule = await member.PostAsJsonAsync($"/api/bookings/{original.Id}/reschedule", new { startsAt = At(15) });
            Assert.Equal(PolicyBlocked, lateReschedule.StatusCode);
            Assert.Equal(At(11), Assert.Single((await Agenda(member)).Upcoming, booking => booking.Id == original.Id).StartsAt);

            app.SetTime(At(10));
            using var atCancelCutoff = await member.DeleteAsync($"/api/bookings/{original.Id}");
            Assert.Equal(HttpStatusCode.NoContent, atCancelCutoff.StatusCode);
            app.SetTime(At(12, 1));
            using var lateCancel = await member.DeleteAsync($"/api/bookings/{later.Id}");
            Assert.Equal(PolicyBlocked, lateCancel.StatusCode);
            Assert.Equal(later.Id, Assert.Single((await Agenda(member)).Upcoming).Id);
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    [Fact]
    public async Task GuestCanMoveAtTheLimitAndCanOnlyCancelTheirOwnReceipt()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = app.CreateClient();
        using var stranger = app.CreateClient();
        var id = await Create(admin, "policy-guest", new(MaxActiveBookings: 1, CancellationNoticeMinutes: 60));
        var link = await Publish(admin, id);
        var address = $"/api/public/resources/{link}";
        using var first = await owner.PostAsJsonAsync(address + "/bookings", new { startsAt = At(11) });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        var capped = (await owner.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(address))!;
        Assert.Empty(capped.Slots);
        Assert.NotNull(capped.BookingMessage);
        using var denied = await owner.PostAsJsonAsync(address + "/bookings", new { startsAt = At(13) });
        Assert.Equal(PolicyBlocked, denied.StatusCode);

        var bookingPath = address + $"/bookings/{receipt.Id}";
        var picks = (await owner.GetFromJsonAsync<RescheduleView>(bookingPath + "/reschedule"))!;
        Assert.Contains(At(13), picks.Starts);
        using var moved = await owner.PostAsJsonAsync(bookingPath + "/reschedule", new { startsAt = At(13) });
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        var newReceipt = (await moved.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.Equal(At(13), newReceipt.StartsAt);
        var latestPath = address + $"/bookings/{newReceipt.Id}";
        using var wrongOwner = await stranger.DeleteAsync(latestPath);
        Assert.Equal(HttpStatusCode.NotFound, wrongOwner.StatusCode);
        using var cancelled = await owner.DeleteAsync(latestPath);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var reopened = (await owner.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(address))!;
        Assert.Contains(At(13), reopened.Slots);
    }

    [Fact]
    public async Task WaitlistSkipsAFullQuotaAndCountsBothHoldsAndPendingApprovals()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var first = await app.CreateGuest();
        using var next = await app.CreateGuest();
        var id = await Create(admin, "policy-queue", new(MaxActiveBookings: 1), requiresApproval: true);
        var held = await CreateBooking(owner, id, At(9));
        foreach (var guest in new[] { first, next })
        {
            using var joined = await guest.PostAsJsonAsync("/api/waitlist", new { resourceId = id, startsAt = At(9) });
            Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        }
        await CreateBooking(first, id, At(11));
        using var released = await owner.DeleteAsync($"/api/bookings/{held.Id}");
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

        var reserve = Assert.Single((await Agenda(next)).Upcoming);
        Assert.Equal(At(9), reserve.StartsAt);
        Assert.NotNull(reserve.HoldExpiresAt);
        Assert.Empty((await Agenda(first)).Waiting);
        using var whileHeld = await Book(next, id, At(13));
        Assert.Equal(PolicyBlocked, whileHeld.StatusCode);
        using var confirmed = await next.PostAsync($"/api/bookings/{reserve.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        using var pending = await Book(next, id, At(13));
        Assert.Equal(PolicyBlocked, pending.StatusCode);
        Assert.Equal("pending", Assert.Single((await Agenda(next)).Upcoming).ApprovalStatus);
    }

    [Fact]
    public async Task InvalidRulesAreRejectedAndLegacyGraphUpdatesRetainThePolicy()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var initial = new BookingPolicy(MinimumNoticeMinutes: 30, BookingWindowDays: 2, MaxActiveBookings: 3);
        var id = await Create(admin, "policy-validation", initial);
        var path = $"/api/workspace/resources/{id}/schedule";
        var graph = (await admin.GetFromJsonAsync<ResourceSchedule>(path))!;
        foreach (var invalid in new[]
        {
            initial with { BookingWindowDays = 0 },
            initial with { MinimumNoticeMinutes = 3000 },
            initial with { MaxActiveBookings = 0 },
            initial with { CancellationNoticeMinutes = -1 }
        })
        {
            using var result = await admin.PutAsJsonAsync(path, graph with { Policy = invalid });
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }
        using var forbidden = await member.PutAsJsonAsync(path, graph with { Policy = BookingPolicy.Standard });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var graphOnly = await admin.PutAsJsonAsync(path, graph with { Policy = null });
        Assert.Equal(HttpStatusCode.OK, graphOnly.StatusCode);
        Assert.Equal(initial, (await admin.GetFromJsonAsync<ResourceSchedule>(path))!.Policy);
    }

    private static async Task<string> Create(HttpClient admin, string tag, BookingPolicy policy, bool requiresApproval = false)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name = tag, location = "Тестовый зал", requiresApproval, details = new ResourceDetails("room", 4, [tag]) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Resource>())!.Id;
        var open = new DayHours([new(480, 1080)], []);
        var schedule = new ResourceSchedule(Enumerable.Repeat(open, 7).ToArray(), [],
            Booking: new BookingRules(true), Policy: policy);
        using var saved = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/schedule", schedule);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return id;
    }

    private static async Task<Guid> Publish(HttpClient admin, string id)
    {
        using var response = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Link>())!.PublicLinkId;
    }

    private static DateTimeOffset At(int hour, int minute = 0, int offsetDays = 0) =>
        Schedule.AtTime(Day.AddDays(offsetDays), new TimeOnly(hour, minute));
    private static Task<HttpResponseMessage> Book(HttpClient client, string resourceId, DateTimeOffset startsAt) =>
        client.PostAsJsonAsync("/api/bookings", new { resourceId, startsAt });
    private static async Task<CreatedBooking> CreateBooking(HttpClient client, string resourceId, DateTimeOffset startsAt)
    {
        using var response = await Book(client, resourceId, startsAt);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
    }

    private static async Task<AgendaView> Agenda(HttpClient client) =>
        (await client.GetFromJsonAsync<AgendaView>("/api/workspace/me"))!;
    private sealed record CalendarView(CalendarDay[] Days);
    private sealed record CalendarDay(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status);
    private sealed record Link(Guid PublicLinkId);
    private sealed record AgendaView(BookingRow[] Upcoming, WaitingRow[] Waiting);
    private sealed record BookingRow(Guid Id, string ResourceId, DateTimeOffset StartsAt, string? ApprovalStatus,
        DateTimeOffset? HoldExpiresAt, BookingChangeTimes? Changes);
    private sealed record WaitingRow(long Id);
}

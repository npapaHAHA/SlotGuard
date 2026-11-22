using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace SlotGuard.Tests;

public sealed class FlexibleBookingTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateOnly Day = new(2026, 5, 12);

    [Fact]
    public void FlexibleDurationCanFitAcrossTheRepeatedHourEvenWhenTheWallClockMovesBack()
    {
        var day = new DateOnly(2026, 11, 1);
        var closed = new DayHours([], []);
        var open = new DayHours([new(60, 120)], []);
        var definition = new ResourceSchedule(Enumerable.Repeat(closed, 7).ToArray(), [new(day, open)],
            "America/New_York", new BookingRules(true));
        var starts = Schedule.StartsOn(day, definition, 30).ToArray();
        Assert.Contains(new DateTimeOffset(2026, 11, 1, 5, 45, 0, TimeSpan.Zero), starts);
        Assert.DoesNotContain(new DateTimeOffset(2026, 11, 1, 6, 45, 0, TimeSpan.Zero), starts);
    }

    [Fact]
    public async Task TwentyOverlappingRequestsWithDifferentStartsAndLengthsCreateOneBooking()
    {
        using var admin = await app.CreateDemo("university-admin");
        var resource = await Create(admin, "flex-race");
        using var member = await app.CreateGuest();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 20).Select(async index =>
        {
            var choice = index % 3;
            await gate.Task;
            using var response = await Book(member, resource, At(9, 15 + choice * 15), Schedule.Durations[2 - choice]);
            return response.StatusCode;
        }).ToArray();
        gate.SetResult();
        var result = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Single(result, status => status == HttpStatusCode.Created);
        Assert.Equal(19, result.Count(status => status == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task BuffersBlockAdjacentTimesUntilTheirExactBoundaryAcrossCalendarSearchAndPublicLink()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var resource = await Create(admin, "flex-buffer", new BookingRules(true, 15, 15),
            new DayHours([new(540, 840)], [new(720, 750)]));
        var link = await Publish(admin, resource);
        using var early = await Book(member, resource, At(9, 15), 30);
        Assert.Equal(HttpStatusCode.Created, early.StatusCode);
        var first = (await early.Content.ReadFromJsonAsync<CreatedBooking>())!;
        Assert.Equal(At(9, 45), first.EndsAt);

        using var collidesWithBuffer = await Book(member, resource, At(10, 0), 60);
        Assert.Equal(HttpStatusCode.Conflict, collidesWithBuffer.StatusCode);
        using var crossesBreak = await Book(member, resource, At(10, 45), 90);
        Assert.Equal(HttpStatusCode.BadRequest, crossesBreak.StatusCode);
        using var offGrid = await Book(member, resource, At(10, 16), 60);
        Assert.Equal(HttpStatusCode.BadRequest, offGrid.StatusCode);

        var exact = (await member.GetFromJsonAsync<AvailabilitySearch.SearchResult>(
            $"/api/availability?date={Day:yyyy-MM-dd}&time=10:00&durationMinutes=60&equipment=flex-buffer"))!;
        Assert.Empty(exact.Exact);
        Assert.Equal(At(10, 15), exact.Alternatives[0].StartsAt);
        Assert.All(exact.Alternatives, item => Assert.Equal(item.StartsAt.AddHours(1), item.EndsAt));

        var calendar = await Calendar(member, resource, 60);
        Assert.Equal([30, 60, 90], calendar.DurationOptions);
        Assert.Equal("booked", Slot(calendar, At(10, 0)).Status);
        Assert.Equal("available", Slot(calendar, At(10, 15)).Status);
        var available = (await guest.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(
            $"/api/public/resources/{link}?durationMinutes=60"))!;
        Assert.DoesNotContain(At(10, 0), available.Slots);
        Assert.Contains(At(10, 15), available.Slots);

        using var boundary = await guest.PostAsJsonAsync($"/api/public/resources/{link}/bookings",
            new { startsAt = At(10, 15), durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, boundary.StatusCode);
        var receipt = (await boundary.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.Equal(At(11, 15), receipt.EndsAt);
    }

    [Fact]
    public async Task CancellingALongBookingOffersBothNonOverlappingRequestsAtTheirOwnStarts()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var first = await app.CreateGuest();
        using var second = await app.CreateGuest();
        var resource = await Create(admin, "flex-wait");
        using var booked = await Book(owner, resource, At(9, 0), 90);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var original = (await booked.Content.ReadFromJsonAsync<CreatedBooking>())!;

        foreach (var (member, start) in new[] { (first, At(9, 15)), (second, At(10, 0)) })
        {
            using var joined = await member.PostAsJsonAsync("/api/waitlist",
                new { resourceId = resource, startsAt = start, durationMinutes = 30 });
            Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        }
        using var cancelled = await owner.DeleteAsync($"/api/bookings/{original.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        foreach (var (member, start) in new[] { (first, At(9, 15)), (second, At(10, 0)) })
        {
            var offer = Slot(await Calendar(member, resource, 30), start);
            Assert.Equal("held", offer.Status);
            Assert.Equal(start.AddMinutes(30), offer.EndsAt);
            Assert.Equal(SlotGuardFixture.Now.AddMinutes(10), offer.HoldExpiresAt);
            using var confirmed = await member.PostAsync($"/api/bookings/{offer.BookingId}/confirm", null);
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }
    }

    [Fact]
    public async Task ContinuousMaintenanceAndIntersectingBookingCannotBothWin()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await Create(admin, "flex-maintenance");
        var maintenance = admin.PostAsJsonAsync("/api/maintenance/window", new
        {
            resourceId = resource, date = Day, fromTime = "10:30", untilTime = "11:15", reason = "Калибровка"
        });
        var booking = Book(member, resource, At(10, 0), 60);
        using var work = await maintenance;
        using var visit = await booking;
        var statuses = new[] { work.StatusCode, visit.StatusCode };
        Assert.Single(statuses, status => status == HttpStatusCode.Created);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        if (work.StatusCode == HttpStatusCode.Created)
        {
            var slot = Slot(await Calendar(admin, resource, 60), At(10, 0));
            Assert.Equal("maintenance", slot.Status);
            Assert.Equal(At(10, 30), slot.MaintenanceStartsAt);
            Assert.Equal(At(11, 15), slot.MaintenanceEndsAt);
        }
    }

    [Fact]
    public async Task PostgresRejectsOverlapsEvenWhenBypassingTheBookingService()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await Create(admin, "flex-exclusion");
        using var created = await Book(member, resource, At(9, 0), 60);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var overlapping = new Booking
        {
            Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), ResourceId = resource,
            StartUtc = At(9, 15), EndUtc = At(10, 45), OccupiedFromUtc = At(9, 15), OccupiedUntilUtc = At(10, 45)
        };
        db.Bookings.Add(overlapping);
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(conflict.InnerException);
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, postgres.SqlState);
        Assert.Equal(BookingDb.ActiveWindowConstraint, postgres.ConstraintName);
    }

    private static DateTimeOffset At(int hour, int minute) => Schedule.AtTime(Day, new TimeOnly(hour, minute));

    private static Task<HttpResponseMessage> Book(HttpClient client, string resourceId, DateTimeOffset startsAt, int durationMinutes) =>
        client.PostAsJsonAsync("/api/bookings", new { resourceId, startsAt, durationMinutes });

    private static async Task<string> Create(HttpClient admin, string tag, BookingRules? rules = null, DayHours? hours = null)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name = tag, location = "Тестовый зал", details = new ResourceDetails("room", 4, [tag]) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<Resource>())!;
        var closed = new DayHours([], []);
        var definition = new ResourceSchedule(Enumerable.Repeat(closed, 7).ToArray(), [new ScheduleException(
            Day, hours ?? new DayHours([new(540, 840)], []))], Booking: rules ?? new BookingRules(true));
        using var configured = await admin.PutAsJsonAsync($"/api/workspace/resources/{item.Id}/schedule", definition);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        return item.Id;
    }

    private static async Task<Guid> Publish(HttpClient admin, string id)
    {
        using var result = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return (await result.Content.ReadFromJsonAsync<Link>())!.PublicLinkId;
    }

    private static async Task<CalendarView> Calendar(HttpClient member, string resource, int duration) =>
        (await member.GetFromJsonAsync<CalendarView>($"/api/calendar?resourceId={resource}&durationMinutes={duration}"))!;

    private static CalendarSlot Slot(CalendarView view, DateTimeOffset start) =>
        Assert.Single(view.Days.SelectMany(day => day.Slots), slot => slot.StartsAt == start);

    private sealed record Link(Guid PublicLinkId);
    private sealed record CalendarView(int[] DurationOptions, CalendarDay[] Days);
    private sealed record CalendarDay(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, Guid? BookingId,
        DateTimeOffset? HoldExpiresAt, DateTimeOffset? MaintenanceStartsAt, DateTimeOffset? MaintenanceEndsAt);
}

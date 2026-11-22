using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class SlotDiagnosticsTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateOnly Monday = new(2026, 5, 11);

    [Fact]
    public async Task ExplainsMaintenanceAndBuffersAndSuggestsBookableIntervalsOfTheSameDuration()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await Create(admin, "Диагностика обслуживания");
        var open = new DayHours([new(540, 1080)], [new(720, 780)]);
        await ScheduleResource(admin, resource, new ResourceSchedule(OpenWeek(open), [],
            Booking: new BookingRules(Flexible: true, BufferBeforeMinutes: 15, BufferAfterMinutes: 15)));
        using var block = await admin.PostAsJsonAsync("/api/maintenance/window", new
        {
            resourceId = resource, date = Monday, fromTime = "10:00", untilTime = "11:00", reason = "Калибровка"
        });
        Assert.Equal(HttpStatusCode.Created, block.StatusCode);
        var overlap = await Diagnose(member, resource, At(10, 45), duration: 30);
        var reason = Assert.Single(overlap.Reasons);
        Assert.Equal("maintenance", reason.Code);
        Assert.Equal("Калибровка", reason.Detail);

        var buffer = await Diagnose(member, resource, At(11), duration: 30);
        Assert.Equal("buffer", Assert.Single(buffer.Reasons).Code);
        Assert.Equal(At(11, 15), buffer.Alternatives.First().StartsAt);
        Assert.All(buffer.Alternatives, slot =>
        {
            Assert.Equal(TimeSpan.FromMinutes(30), slot.EndsAt - slot.StartsAt);
            Assert.True(slot.StartsAt.AddMinutes(-15) >= At(11) || slot.EndsAt.AddMinutes(15) <= At(10));
        });
        using var booked = await Book(member, resource, buffer.Alternatives.First().StartsAt, 30);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        Assert.Equal("break", Assert.Single((await Diagnose(member, resource, At(12), duration: 30)).Reasons).Code);
    }

    [Fact]
    public async Task DistinguishesHoursExceptionsLeadTimeHorizonAndQuota()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await Create(admin, "Диагностика графика");
        var week = OpenWeek(new DayHours([new(540, 1020)], [new(720, 780)]));
        var closed = new DayHours([], []);
        week[2] = closed;
        await ScheduleResource(admin, resource, new ResourceSchedule(week, [new(Monday.AddDays(3), closed)],
            Booking: new BookingRules(Flexible: true),
            Policy: new BookingPolicy(MinimumNoticeMinutes: 60, BookingWindowDays: 14, MaxActiveBookings: 1)));
        var expected = new Dictionary<DateTimeOffset, string>
        {
            [At(8)] = "past", [At(9)] = "advance-notice", [At(10, 7)] = "off-grid",
            [At(11, 45)] = "does-not-fit", [At(12)] = "break", [At(17)] = "outside-hours",
            [At(10, days: 2)] = "closed-day", [At(10, days: 3)] = "schedule-exception",
            [At(10, days: 15)] = "booking-window"
        };
        foreach (var (time, code) in expected)
            Assert.Equal(code, Assert.Single((await Diagnose(member, resource, time)).Reasons).Code);

        var lunch = await Diagnose(member, resource, At(12));
        Assert.DoesNotContain(lunch.Alternatives, slot => slot.StartsAt < At(13) && slot.EndsAt > At(12));
        Assert.Equal(At(13), lunch.Alternatives.First().StartsAt);
        using var booked = await Book(member, resource, At(10));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var limit = await Diagnose(member, resource, At(14));
        Assert.Equal("active-limit", Assert.Single(limit.Reasons).Code);
        Assert.NotNull(limit.BookingMessage);
        Assert.Empty(limit.Alternatives);
    }

    [Fact]
    public async Task ChecksOrganizationAndLimitsPrivateDetailsToOwnersAndAdmins()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var other = await app.CreateGuest();
        using var foreign = await app.CreateDemo("coworking-admin");
        var resource = await Create(admin, "Диагностика заявки", requiresApproval: true);
        using var pending = await Book(owner, resource, At(9));
        Assert.Equal(HttpStatusCode.Created, pending.StatusCode);
        Assert.Equal("pending", Assert.Single((await Diagnose(owner, resource, At(9))).Reasons).Code);
        Assert.Equal("pending", Assert.Single((await Diagnose(admin, resource, At(9))).Reasons).Code);
        var masked = Assert.Single((await Diagnose(other, resource, At(9))).Reasons);
        Assert.Equal("occupied", masked.Code);
        Assert.Null(masked.Detail);
        using var denied = await foreign.GetAsync(Address(resource, At(9)));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var heldResource = await Create(admin, "Диагностика резерва");
        using var first = await Book(owner, heldResource, At(11));
        var booking = (await first.Content.ReadFromJsonAsync<CreatedBooking>())!;
        using var joined = await other.PostAsJsonAsync("/api/waitlist", new { resourceId = heldResource, startsAt = At(11) });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        using var cancelled = await owner.DeleteAsync($"/api/bookings/{booking.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var hold = await Diagnose(other, heldResource, At(11));
        Assert.Equal("reserved", Assert.Single(hold.Reasons).Code);
        Assert.DoesNotContain(hold.Alternatives, slot => slot.StartsAt == At(11));
        Assert.Equal("occupied", Assert.Single((await Diagnose(owner, heldResource, At(11))).Reasons).Code);
    }

    [Fact]
    public async Task ExplainsMissingAndAmbiguousCivilTimes()
    {
        using var admin = await app.CreateDemo("university-admin");
        var resource = await Create(admin, "Диагностика перехода часов");
        await ScheduleResource(admin, resource, new ResourceSchedule(OpenWeek(new DayHours([new(0, 600)], [])), [],
            "America/New_York", new BookingRules(Flexible: true)));
        try
        {
            app.SetTime(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero));
            using var spring = await app.CreateGuest();
            var gap = await spring.GetFromJsonAsync<SlotDiagnostics.View>(
                $"/api/resources/{resource}/slot-diagnostics?date=2026-03-08&time=02:30&timeZone=America%2FNew_York&durationMinutes=30");
            Assert.Equal("clock-change", Assert.Single(gap!.Reasons).Code);
            Assert.NotEmpty(gap.Alternatives);

            app.SetTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
            using var fall = await app.CreateGuest();
            var repeated = await fall.GetFromJsonAsync<SlotDiagnostics.View>(
                $"/api/resources/{resource}/slot-diagnostics?date=2026-11-01&time=01:30&timeZone=America%2FNew_York&durationMinutes=30");
            Assert.Equal(2, repeated!.Reasons.Length);
            Assert.Equal(TimeSpan.FromHours(1), repeated.Reasons[1].StartsAt - repeated.Reasons[0].StartsAt);
            Assert.All(repeated.Reasons, item => Assert.Equal("available", item.Code));
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    private static DateTimeOffset At(int hour, int minute = 0, int days = 0) =>
        Schedule.AtTime(Monday.AddDays(days), new TimeOnly(hour, minute));

    private static DayHours[] OpenWeek(DayHours? open = null) =>
        Enumerable.Repeat(open ?? new DayHours([new(540, 1080)], []), 7).ToArray();

    private static Task<HttpResponseMessage> Book(HttpClient member, string resource, DateTimeOffset start, int duration = 60) =>
        member.PostAsJsonAsync("/api/bookings", new { resourceId = resource, startsAt = start, durationMinutes = duration });

    private static string Address(string resource, DateTimeOffset start, int duration = 60) =>
        $"/api/resources/{resource}/slot-diagnostics?startsAt={Uri.EscapeDataString(start.ToString("O"))}&durationMinutes={duration}";

    private static async Task<SlotDiagnostics.View> Diagnose(HttpClient member, string resource, DateTimeOffset start, int duration = 60)
    {
        using var response = await member.GetAsync(Address(resource, start, duration));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<SlotDiagnostics.View>())!;
    }

    private static async Task<string> Create(HttpClient admin, string name, bool requiresApproval = false)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name, location = "Корпус А", requiresApproval });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Resource>())!.Id;
    }

    private static async Task ScheduleResource(HttpClient admin, string resource, ResourceSchedule schedule)
    {
        using var response = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource}/schedule", schedule);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

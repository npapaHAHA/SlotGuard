using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class TimeZoneTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private const string NewYork = "America/New_York";

    [Fact]
    public async Task ALocalMondayInTokyoIsSundayForViewersInCalifornia()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var id = await CreateResource(admin, "tokyo-qa");
        var localDay = new DateOnly(2026, 5, 18);
        var sunday = localDay.AddDays(-1);
        const string tokyo = "Asia/Tokyo";
        const string california = "America/Los_Angeles";
        await SetSchedule(admin, id, OneDay(tokyo, localDay, new SchedulePeriod(30, 150)));
        var link = await Publish(admin, id);
        var early = Utc(2026, 5, 17, 15, 30);
        var late = early.AddHours(1);

        var viewer = await Calendar(member, id, california, "2026-05-11");
        Assert.Equal(tokyo, viewer.ResourceTimeZone);
        Assert.Equal([early, late], Assert.Single(viewer.Days, day => day.Date == sunday).Slots.Select(slot => slot.StartsAt));
        var venue = await Calendar(member, id, tokyo, "2026-05-18");
        Assert.Equal([early, late], venue.Days[0].Slots.Select(slot => slot.StartsAt));

        var search = await Search(member, sunday, "08:30", california, "tokyo-qa");
        Assert.Equal(early, Assert.Single(search.Exact).StartsAt);
        var published = await ReadPublic(guest, link, california);
        Assert.Equal(tokyo, published.ResourceTimeZone);
        Assert.Equal([early, late], published.Slots);

        using var block = await admin.PostAsJsonAsync("/api/maintenance/window", new
        {
            resourceId = id, date = localDay, fromTime = "00:30", throughTime = "00:30", reason = "Сервис"
        });
        Assert.Equal(HttpStatusCode.Created, block.StatusCode);
        using var booking = await member.PostAsJsonAsync("/api/bookings", new { resourceId = id, startsAt = late });
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
        Assert.Empty((await ReadPublic(guest, link, tokyo)).Slots);

        var overview = (await admin.GetFromJsonAsync<Overview>($"/api/workspace/overview?timeZone={california}&week=2026-05-11"))!;
        var capacity = Assert.Single(overview.Resources, row => row.Id == id).Counts;
        Assert.Equal(new Counts(0, 1, 1), capacity);
    }

    [Fact]
    public async Task SpringForwardSkipsMissingLocalTimesAndKeepsSixtyMinuteBookings()
    {
        app.SetTime(Utc(2026, 3, 6, 12));
        try
        {
            using var admin = await app.CreateDemo("university-admin");
            using var member = await app.CreateGuest();
            using var guest = app.CreateClient();
            var id = await CreateResource(admin, "spring-qa");
            var day = new DateOnly(2026, 3, 8);
            var hours = new DayHours([new(60, 300)], [new(180, 240)]);
            await SetSchedule(admin, id, OneDay(NewYork, day, hours));
            var link = await Publish(admin, id);
            var beforeJump = Utc(2026, 3, 8, 6); // 01:00 EST to 03:00 EDT
            var afterBreak = Utc(2026, 3, 8, 8);
            Assert.Equal([beforeJump, afterBreak], (await ReadPublic(guest, link, NewYork)).Slots);
            using var gap = await member.GetAsync(SearchAddress(day, "02:30", NewYork, "spring-qa"));
            Assert.Equal(HttpStatusCode.BadRequest, gap.StatusCode);
            Assert.Contains("перевода часов", (await gap.Content.ReadFromJsonAsync<Problem>())!.Title);
            Assert.Empty((await Search(member, day, "03:00", NewYork, "spring-qa")).Exact);
            using var booked = await guest.PostAsJsonAsync($"/api/public/resources/{link}/bookings", new { startsAt = beforeJump });
            Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
            Assert.Equal(afterBreak, Assert.Single((await ReadPublic(guest, link, NewYork)).Slots));
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    [Fact]
    public async Task FallBackReturnsBothOccurrencesAndTheyCanBeBookedIndependently()
    {
        app.SetTime(Utc(2026, 10, 31, 12));
        try
        {
            using var admin = await app.CreateDemo("university-admin");
            using var member = await app.CreateGuest();
            using var guest = app.CreateClient();
            var id = await CreateResource(admin, "fall-qa");
            var day = new DateOnly(2026, 11, 1);
            await SetSchedule(admin, id, OneDay(NewYork, day, new SchedulePeriod(60, 180)));
            var first = Utc(2026, 11, 1, 5);
            var second = first.AddHours(1);
            var third = second.AddHours(1);
            var link = await Publish(admin, id);
            Assert.Equal([first, second, third], (await ReadPublic(guest, link, NewYork)).Slots);
            var exact = (await Search(member, day, "01:00", NewYork, "fall-qa")).Exact;
            Assert.Equal([first, second], exact.Select(slot => slot.StartsAt));

            var bookings = await Task.WhenAll(exact.Select(slot =>
                member.PostAsJsonAsync("/api/bookings", new { resourceId = id, startsAt = slot.StartsAt })));
            foreach (var booking in bookings)
            {
                using (booking) Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
            }
            Assert.Equal(third, Assert.Single((await ReadPublic(guest, link, NewYork)).Slots));
            Assert.Empty((await Search(member, day, "01:00", NewYork, "fall-qa")).Exact);
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    [Fact]
    public async Task InvalidZonesAndAChangeThatWouldMoveAnExistingBookingAreRejected()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var id = await CreateResource(admin, "zone-qa");
        var day = new DateOnly(2026, 5, 12);
        const string kolkata = "Asia/Kolkata";
        var definition = OneDay(kolkata, day, new SchedulePeriod(600, 720));
        await SetSchedule(admin, id, definition);
        var originalStart = Utc(2026, 5, 12, 4, 30);
        using var booked = await member.PostAsJsonAsync("/api/bookings", new { resourceId = id, startsAt = originalStart });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        using var change = await admin.PutAsJsonAsync(ScheduleAddress(id), definition with { TimeZone = NewYork });
        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Equal(originalStart, Assert.Single((await change.Content.ReadFromJsonAsync<Conflict>())!.Conflicts));
        using var invalid = await admin.PutAsJsonAsync(ScheduleAddress(id), definition with { TimeZone = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidQuery = await member.GetAsync($"/api/calendar?resourceId={id}&timeZone=Mars/Olympus");
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuery.StatusCode);

        await SetSchedule(admin, id, definition with { TimeZone = null });
        var saved = (await admin.GetFromJsonAsync<ResourceSchedule>(ScheduleAddress(id)))!;
        Assert.Equal(kolkata, saved.TimeZone);
        var view = await Calendar(member, id, "UTC", "2026-05-11");
        Assert.Equal("mine", Assert.Single(view.Days.SelectMany(item => item.Slots), slot => slot.StartsAt == originalStart).Status);
    }

    [Fact]
    public void ThirtyMinuteClockRollbackCannotProduceOverlappingSlots()
    {
        var day = new DateOnly(2026, 4, 5);
        var schedule = OneDay("Australia/Lord_Howe", day, new SchedulePeriod(90, 240));
        var starts = Schedule.StartsOn(day, schedule).ToArray();
        Assert.Equal([Utc(2026, 4, 4, 14, 30), Utc(2026, 4, 4, 16)], starts);
    }

    private static ResourceSchedule OneDay(string zone, DateOnly day, SchedulePeriod hours) =>
        OneDay(zone, day, new DayHours([hours], []));

    private static ResourceSchedule OneDay(string zone, DateOnly day, DayHours hours) =>
        new(Enumerable.Repeat(new DayHours([], []), 7).ToArray(), [new(day, hours)], zone);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string ScheduleAddress(string id) => $"/api/workspace/resources/{id}/schedule";

    private static async Task SetSchedule(HttpClient admin, string id, ResourceSchedule schedule)
    {
        using var result = await admin.PutAsJsonAsync(ScheduleAddress(id), schedule);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    private static async Task<string> CreateResource(HttpClient admin, string tag)
    {
        using var result = await admin.PostAsJsonAsync("/api/workspace/resources", new
        {
            name = "Международная переговорная", location = "Здание 1", details = new ResourceDetails("room", 6, [tag])
        });
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        return (await result.Content.ReadFromJsonAsync<Resource>())!.Id;
    }

    private static async Task<Guid> Publish(HttpClient admin, string id)
    {
        using var result = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return (await result.Content.ReadFromJsonAsync<Link>())!.PublicLinkId;
    }

    private static string SearchAddress(DateOnly day, string time, string zone, string tag) =>
        $"/api/availability?date={day:yyyy-MM-dd}&time={time}&timeZone={zone}&equipment={tag}";

    private static async Task<AvailabilitySearch.SearchResult> Search(HttpClient member, DateOnly day, string time, string zone, string tag) =>
        (await member.GetFromJsonAsync<AvailabilitySearch.SearchResult>(SearchAddress(day, time, zone, tag)))!;

    private static async Task<PublicBookingEndpoints.PublicResource> ReadPublic(HttpClient guest, Guid id, string zone) =>
        (await guest.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>($"/api/public/resources/{id}?timeZone={zone}"))!;

    private static async Task<CalendarView> Calendar(HttpClient member, string id, string zone, string week) =>
        (await member.GetFromJsonAsync<CalendarView>($"/api/calendar?resourceId={id}&timeZone={zone}&week={week}"))!;

    private sealed record Link(Guid PublicLinkId);
    private sealed record Conflict(DateTimeOffset[] Conflicts);
    private sealed record Problem(string Title);
    private sealed record CalendarView(string ResourceTimeZone, Day[] Days);
    private sealed record Day(DateOnly Date, CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status);
    private sealed record Overview(OverviewResource[] Resources);
    private sealed record OverviewResource(string Id, Counts Counts);
    private sealed record Counts(int Free, int Booked, int Maintenance);
}

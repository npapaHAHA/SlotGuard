using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class ResourceScheduleTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateOnly Monday = new(2026, 5, 11);
    private static readonly DayHours Closed = new([], []);
    private static readonly DayHours MondayHours = new([new(570, 990)], [new(690, 750), new(870, 900)]);

    [Fact]
    public async Task CalendarSearchPublicLinkAndOverviewUseResourceHoursAndDateExceptions()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var resource = await CreateResource(admin, tag: "график-qa");
        var link = await Publish(admin, resource.Id);
        var shortDay = new ScheduleException(Monday.AddDays(1), new DayHours([new(1110, 1260)], []));
        var holiday = new ScheduleException(Monday.AddDays(7), Closed);
        var schedule = Weekly(MondayHours, holiday, shortDay);
        using var saved = await Put(admin, resource.Id, schedule);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var stored = (await admin.GetFromJsonAsync<ResourceSchedule>(Address(resource.Id)))!;
        Assert.Equal([shortDay.Date, holiday.Date], stored.Exceptions.Select(item => item.Date));

        var calendar = await ReadCalendar(member, resource.Id, Monday);
        var mondayStarts = new[] { At(9, 30), At(10, 30), At(12, 30), At(13, 30), At(15, 0) };
        var tuesdayStarts = new[] { At(18, 30, 1), At(19, 30, 1) };
        Assert.Equal(mondayStarts, calendar.Days[0].Slots.Select(slot => slot.StartsAt));
        Assert.Equal(tuesdayStarts, calendar.Days[1].Slots.Select(slot => slot.StartsAt));
        Assert.All(calendar.Days[2..], day => Assert.Empty(day.Slots));
        Assert.Empty((await ReadCalendar(member, resource.Id, holiday.Date)).Days[0].Slots);
        var defaultCalendar = await ReadCalendar(member, "microscope", Monday);
        Assert.Equal([At(9, 0), At(11, 0), At(13, 0), At(15, 0), At(17, 0)],
            defaultCalendar.Days[0].Slots.Select(slot => slot.StartsAt));

        var exact = await Search(member, "09:30");
        Assert.Equal(resource.Id, Assert.Single(exact.Exact).Resource.Id);
        var duringBreak = await Search(member, "11:30");
        Assert.Empty(duringBreak.Exact);
        Assert.Equal(At(12, 30), duringBreak.Alternatives[0].StartsAt);

        var published = (await guest.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(PublicAddress(link)))!;
        Assert.Equal(mondayStarts.Concat(tuesdayStarts), published.Slots.Where(time => Schedule.LocalDate(time) < holiday.Date));
        Assert.DoesNotContain(published.Slots, time => Schedule.LocalDate(time) == holiday.Date);
        var overview = (await admin.GetFromJsonAsync<Overview>("/api/workspace/overview"))!;
        Assert.Equal(7, Assert.Single(overview.Resources, row => row.Id == resource.Id).Counts.Free);

        var duringGap = At(14, 30);
        using var invalidBooking = await Book(member, resource.Id, duringGap);
        Assert.Equal(HttpStatusCode.BadRequest, invalidBooking.StatusCode);
        using var invalidQueue = await member.PostAsJsonAsync("/api/waitlist", new { resourceId = resource.Id, startsAt = duringGap });
        Assert.Equal(HttpStatusCode.BadRequest, invalidQueue.StatusCode);
        using var invalidGuest = await guest.PostAsJsonAsync($"{PublicAddress(link)}/bookings", new { startsAt = duringGap });
        Assert.Equal(HttpStatusCode.BadRequest, invalidGuest.StatusCode);
        using var booked = await guest.PostAsJsonAsync($"{PublicAddress(link)}/bookings", new { startsAt = tuesdayStarts[0] });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
    }

    [Fact]
    public async Task OnlyTheOrganizationAdminCanReadAndWriteAndInvalidIntervalsAreRejected()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var other = await app.CreateDemo("coworking-admin");
        other.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, "coworking");
        var resource = await CreateResource(admin);
        using var memberGet = await member.GetAsync(Address(resource.Id));
        using var memberPut = await Put(member, resource.Id, Weekly(Closed));
        using var otherGet = await other.GetAsync(Address(resource.Id));
        using var otherPut = await Put(other, resource.Id, Weekly(Closed));
        Assert.Equal(HttpStatusCode.Forbidden, memberGet.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberPut.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherPut.StatusCode);

        var exception = new ScheduleException(Monday, Closed);
        ResourceSchedule[] invalid =
        [
            Weekly(Closed) with { Week = [Closed] },
            Weekly(Closed, exception, exception),
            Weekly(new DayHours([new(540, 720), new(660, 780)], [])),
            Weekly(new DayHours([new(540, 720)], [new(690, 750)])),
            Weekly(new DayHours([new(540, 720)], [new(600, 660), new(630, 690)])),
            Weekly(new DayHours([new(1320, 1500)], []))
        ];
        foreach (var input in invalid)
        {
            using var result = await Put(admin, resource.Id, input);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }
        var calendar = await ReadCalendar(member, resource.Id, Monday);
        Assert.Equal(5, calendar.Days[0].Slots.Length);
    }

    [Fact]
    public async Task MaintenanceAndPendingApprovalProtectSlotsFromAnExceptionUntilReleased()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await CreateResource(admin, requiresApproval: true);
        using var saved = await Put(admin, resource.Id, Weekly(MondayHours));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var blocked = await admin.PostAsJsonAsync("/api/maintenance/window", new
        {
            resourceId = resource.Id, date = Monday, fromTime = "12:30", throughTime = "13:30", reason = "Калибровка"
        });
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
        var window = (await blocked.Content.ReadFromJsonAsync<CreatedMaintenance>())!;
        Assert.Equal(2, window.Count);
        using var booked = await Book(member, resource.Id, At(9, 30));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var booking = (await booked.Content.ReadFromJsonAsync<CreatedBooking>())!;
        Assert.Equal(BookingApproval.Pending, booking.ApprovalStatus);

        var closure = Weekly(MondayHours, new ScheduleException(Monday, Closed));
        using var rejected = await Put(admin, resource.Id, closure);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal([At(9, 30), At(12, 30), At(13, 30)], (await rejected.Content.ReadFromJsonAsync<Conflict>())!.Conflicts);
        var calendar = await ReadCalendar(admin, resource.Id, Monday);
        using var cancelled = await member.DeleteAsync($"/api/bookings/{booking.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        foreach (var slot in calendar.Days[0].Slots.Where(slot => slot.Status == "maintenance"))
        {
            using var released = await admin.DeleteAsync($"/api/maintenance/{slot.MaintenanceId}");
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }
        using var applied = await Put(admin, resource.Id, closure);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Empty((await ReadCalendar(member, resource.Id, Monday)).Days[0].Slots);
    }

    [Fact]
    public async Task ClosingADateAndBookingItsSlotCannotBothSucceed()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await CreateResource(admin);
        var closure = Weekly(MondayHours, new ScheduleException(Monday, Closed));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = Race(() => Put(admin, resource.Id, closure));
        var book = Race(() => Book(member, resource.Id, At(9, 0)));
        gate.SetResult();
        var statuses = await Task.WhenAll(save, book).WaitAsync(TimeSpan.FromSeconds(30));
        var calendar = await ReadCalendar(member, resource.Id, Monday);
        if (statuses[0] == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.BadRequest, statuses[1]);
            Assert.Empty(calendar.Days[0].Slots);
        }
        else
        {
            Assert.Equal([HttpStatusCode.Conflict, HttpStatusCode.Created], statuses);
            Assert.Equal("mine", Assert.Single(calendar.Days[0].Slots, slot => slot.StartsAt == At(9, 0)).Status);
        }

        async Task<HttpStatusCode> Race(Func<Task<HttpResponseMessage>> send)
        {
            await gate.Task;
            using var response = await send();
            return response.StatusCode;
        }
    }

    private static ResourceSchedule Weekly(DayHours monday, params ScheduleException[] exceptions) =>
        new([monday, Closed, Closed, Closed, Closed, Closed, Closed], exceptions);

    private static DateTimeOffset At(int hour, int minute, int offset = 0) =>
        Schedule.AtTime(Monday.AddDays(offset), new TimeOnly(hour, minute));

    private static string Address(string id) => $"/api/workspace/resources/{id}/schedule";
    private static string PublicAddress(Guid link) => $"/api/public/resources/{link}";
    private static Task<HttpResponseMessage> Put(HttpClient admin, string id, ResourceSchedule schedule) =>
        admin.PutAsJsonAsync(Address(id), schedule);

    private static Task<HttpResponseMessage> Book(HttpClient member, string resourceId, DateTimeOffset startsAt) =>
        member.PostAsJsonAsync("/api/bookings", new { resourceId, startsAt });

    private static async Task<Resource> CreateResource(HttpClient admin, bool requiresApproval = false, string? tag = null)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources", new
        {
            name = "Зал для занятий", location = "Второй этаж", requiresApproval,
            details = new ResourceDetails("room", 6, tag is null ? [] : [tag])
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Resource>())!;
    }

    private static async Task<Guid> Publish(HttpClient admin, string id)
    {
        using var response = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Link>())!.PublicLinkId;
    }

    private static async Task<Calendar> ReadCalendar(HttpClient member, string id, DateOnly monday) =>
        (await member.GetFromJsonAsync<Calendar>($"/api/calendar?resourceId={id}&week={monday:yyyy-MM-dd}"))!;

    private static async Task<AvailabilitySearch.SearchResult> Search(HttpClient member, string time) =>
        (await member.GetFromJsonAsync<AvailabilitySearch.SearchResult>(
            $"/api/availability?date={Monday:yyyy-MM-dd}&time={time}&equipment=график-qa"))!;

    private sealed record Link(Guid PublicLinkId);
    private sealed record Conflict(DateTimeOffset[] Conflicts);
    private sealed record Overview(OverviewResource[] Resources);
    private sealed record OverviewResource(string Id, Counts Counts);
    private sealed record Counts(int Free);
    private sealed record Calendar(Day[] Days);
    private sealed record Day(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? MaintenanceId);
}

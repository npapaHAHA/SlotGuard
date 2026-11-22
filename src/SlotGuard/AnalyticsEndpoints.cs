using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class AnalyticsEndpoints
{
    private const int MaximumDays = 93;
    private const int HistoryDays = 730;

    private sealed record BookingRow(Guid Id, string ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
        DateTimeOffset? CancelledAt, DateTimeOffset? HoldExpiresAt, bool Maintenance, string? ApprovalStatus,
        Guid? BundleId, int CalendarSequence, bool Replaced)
    {
        public bool Confirmed => CancelledAt is null && HoldExpiresAt is null && !Maintenance
            && ApprovalStatus is null or BookingApproval.Approved;
        public ReportSpan Span => new(StartUtc, EndUtc);
    }

    private sealed record SeatRow(string ResourceId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status,
        bool WasApproved, string? Attendance, bool SessionCancelled);
    private sealed record QueueRow(string ResourceId, DateTimeOffset StartsAt, bool Offered, bool Converted);
    private sealed record ResourceEvent(string ResourceId, string Action, DateTimeOffset At);
    private sealed record Usage(double AvailableMinutes, double BookedMinutes, double MaintenanceMinutes)
    {
        public static readonly Usage Empty = new(0, 0, 0);
        public static Usage operator +(Usage a, Usage b) => new(
            a.AvailableMinutes + b.AvailableMinutes, a.BookedMinutes + b.BookedMinutes, a.MaintenanceMinutes + b.MaintenanceMinutes);
    }
    private sealed record Metrics(Usage Usage, int Confirmed, int Approved, int Rejected, int Cancelled,
        int Present, int NoShow, int Unmarked, int Waitlisted, int Offered, int Converted);

    public static async Task<IResult> Read(DateOnly? from, DateOnly? through, string? resourceId, string? timeZone,
        BookingDb db, OrganizationScope scope, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!Schedule.TryZone(timeZone, out var zone))
            return BookingEndpoints.InvalidTimeZone();

        var now = clock.GetUtcNow();
        var today = Schedule.LocalDate(now, zone);
        var first = from ?? today.AddDays(-29);
        var last = through ?? today;
        if (first < today.AddDays(-HistoryDays) || last > Schedule.DisplayLastDay(now, zone)
            || last < first || last.DayNumber - first.DayNumber >= MaximumDays)
            return Results.Problem(statusCode: 400, title: "Выберите период до 93 дней в пределах доступных дат.");

        var dates = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays).ToArray();
        var days = dates.Select(day => ReportCalendar.CivilSpans(day, [(0, 1440)], zone)).ToArray();
        var fromUtc = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14);
        var untilUtc = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(14);
        bool InPeriod(DateTimeOffset time)
        {
            var date = Schedule.LocalDate(time, zone);
            return date >= first && date <= last;
        }

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var catalog = await scope.Resources(db).AsNoTracking().OrderBy(item => item.IsArchived)
            .ThenBy(item => item.SortOrder).ThenBy(item => item.Name).ThenBy(item => item.Id).ToArrayAsync(ct);
        var resources = resourceId is null ? catalog : catalog.Where(item => item.Id == resourceId).ToArray();
        if (resourceId is not null && resources.Length == 0)
            return Results.NotFound();

        var ids = resources.Select(item => item.Id).ToArray();
        var relevant = db.Bookings.AsNoTracking().Where(item => ids.Contains(item.ResourceId)
            && item.EndUtc > fromUtc && item.StartUtc < untilUtc);
        var bookings = await relevant.Select(item => new BookingRow(item.Id, item.ResourceId, item.StartUtc, item.EndUtc,
            item.CancelledAt, item.HoldExpiresAt, item.MaintenanceReason != null, item.ApprovalStatus, item.BundleId,
            item.CalendarSequence, item.CancelledAt != null && db.Bookings.Any(moved => moved.RescheduledFromId == item.Id)))
            .ToArrayAsync(ct);
        var seatRows = await (from seat in db.SeatReservations.AsNoTracking()
            join session in relevant on seat.SessionId equals session.Id
            select new SeatRow(session.ResourceId, session.StartUtc, session.EndUtc, seat.Status, seat.WasApproved,
                seat.Attendance, session.CancelledAt != null)).ToArrayAsync(ct);
        var queueRows = await (from entry in db.WaitlistEntries.AsNoTracking()
            join booking in db.Bookings.AsNoTracking() on entry.OfferedBookingId equals (Guid?)booking.Id into offers
            from offer in offers.DefaultIfEmpty()
            where ids.Contains(entry.ResourceId) && entry.StartUtc >= fromUtc && entry.StartUtc < untilUtc
            select new QueueRow(entry.ResourceId, entry.StartUtc, entry.OfferedBookingId != null,
                offer != null && offer.HoldExpiresAt == null
                    && (offer.ApprovalStatus == null || offer.ApprovalStatus == BookingApproval.Approved))).ToArrayAsync(ct);
        var history = (await db.WorkspaceActivities.AsNoTracking().Where(item => ids.Contains(item.ResourceId)
            && item.OrganizationId == scope.Current.Id
            && (item.Action == "resource.created" || item.Action == "resource.archived" || item.Action == "resource.restored"))
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .Select(item => new ResourceEvent(item.ResourceId, item.Action, item.OccurredAt)).ToArrayAsync(ct))
            .ToLookup(item => item.ResourceId);
        await snapshot.CommitAsync(ct);

        var inPeriod = bookings.Where(item => InPeriod(item.StartUtc)).ToArray();
        var seats = seatRows.Where(item => InPeriod(item.StartsAt)).ToArray();
        var queue = queueRows.Where(item => InPeriod(item.StartsAt)).ToArray();
        var byResource = bookings.ToLookup(item => item.ResourceId);
        var startsByResource = inPeriod.ToLookup(item => item.ResourceId);
        var seatsByResource = seats.ToLookup(item => item.ResourceId);
        var queueByResource = queue.ToLookup(item => item.ResourceId);
        var dailyUsage = Enumerable.Repeat(Usage.Empty, days.Length).ToArray();
        var resourceMetrics = resources.Select(resource =>
        {
            ct.ThrowIfCancellationRequested();
            var occupied = byResource[resource.Id];
            var working = ReportCalendar.WorkingHours(Schedule.Read(resource), fromUtc, untilUtc);
            var active = Lifecycle(resource, history[resource.Id], fromUtc, untilUtc);
            var hours = ReportCalendar.Intersect(working, active).ToArray();
            var reserved = ReportCalendar.Intersect(hours, occupied.Where(item => item.Confirmed)
                .OrderBy(item => item.StartUtc).Select(item => item.Span).ToArray()).ToArray();
            var maintenance = ReportCalendar.Intersect(hours, occupied.Where(item => item.Maintenance && item.CancelledAt is null)
                .OrderBy(item => item.StartUtc).Select(item => item.Span).ToArray()).ToArray();
            var usage = Usage.Empty;
            for (var index = 0; index < days.Length; index++)
            {
                var closed = MinutesIn(maintenance, days[index]);
                var available = Math.Max(0, MinutesIn(hours, days[index]) - closed);
                var load = new Usage(available, Math.Min(available, MinutesIn(reserved, days[index])), closed);
                dailyUsage[index] += load;
                usage += load;
            }
            return new { resource.Id, resource.Name, resource.IsArchived,
                Metrics = Summarize(usage, startsByResource[resource.Id], seatsByResource[resource.Id], queueByResource[resource.Id], now) };
        }).ToArray();

        var dailyStarts = new int[days.Length];
        var peak = new int[7 * 24];
        foreach (var booking in inPeriod.Where(item => item.Confirmed))
        {
            var local = TimeZoneInfo.ConvertTime(booking.StartUtc, zone);
            var date = DateOnly.FromDateTime(local.DateTime);
            dailyStarts[date.DayNumber - first.DayNumber]++;
            peak[Schedule.DayIndex(date) * 24 + local.Hour]++;
        }

        return Results.Ok(new
        {
            From = first, Through = last, Today = today, EarliestDate = today.AddDays(-HistoryDays),
            LastDate = Schedule.DisplayLastDay(now, zone), TimeZone = zone.Id, ResourceId = resourceId,
            Catalog = catalog.Select(item => new { item.Id, item.Name, item.IsArchived }),
            Totals = Summarize(dailyUsage.Aggregate(Usage.Empty, (sum, item) => sum + item), inPeriod, seats, queue, now),
            Daily = dates.Select((date, index) => new { Date = date, Usage = dailyUsage[index], Confirmed = dailyStarts[index] }),
            Peak = peak,
            Resources = resourceMetrics
        });
    }

    private static double MinutesIn(ReportSpan[] items, ReportSpan[] day) =>
        ReportCalendar.Intersect(items, day).Sum(item => item.Minutes);

    private static ReportSpan[] Lifecycle(Resource resource, IEnumerable<ResourceEvent> events, DateTimeOffset from,
        DateTimeOffset until)
    {
        var changes = events.ToArray();
        var isActive = changes.Length == 0 ? !resource.IsArchived : changes[0].Action == "resource.archived";
        var hours = new List<ReportSpan>();
        var start = from;
        foreach (var change in changes)
        {
            if (change.At <= from)
            {
                isActive = change.Action != "resource.archived";
                continue;
            }
            if (change.At >= until)
                break;
            if (isActive && change.At > start)
                hours.Add(new ReportSpan(start, change.At));
            isActive = change.Action != "resource.archived";
            start = change.At;
        }
        if (isActive && start < until)
            hours.Add(new ReportSpan(start, until));
        return hours.ToArray();
    }

    private static Metrics Summarize(Usage usage, IEnumerable<BookingRow> bookings, IEnumerable<SeatRow> seats,
        IEnumerable<QueueRow> queue, DateTimeOffset now)
    {
        var rows = bookings.ToArray();
        var decisions = rows.Where(item => item.ApprovalStatus is BookingApproval.Approved or BookingApproval.Rejected)
            .DistinctBy(item => (item.BundleId ?? item.Id, item.CalendarSequence)).ToArray();
        var registrations = seats.ToArray();
        var attendance = registrations.Where(item => item.Status == SeatStatus.Confirmed && !item.SessionCancelled
            && item.EndsAt <= now).ToArray();
        var waits = queue.ToArray();
        var cancellations = rows.Where(item => item.CancelledAt != null && !item.Maintenance && item.HoldExpiresAt is null
            && !item.Replaced && item.ApprovalStatus != BookingApproval.Rejected)
            .DistinctBy(item => (item.BundleId ?? item.Id, item.CalendarSequence)).Count();
        return new Metrics(usage, rows.Count(item => item.Confirmed),
            decisions.Count(item => item.ApprovalStatus == BookingApproval.Approved) + registrations.Count(item => item.WasApproved),
            decisions.Count(item => item.ApprovalStatus == BookingApproval.Rejected) + registrations.Count(item => item.Status == SeatStatus.Rejected),
            cancellations, attendance.Count(item => item.Attendance == SeatStatus.Present),
            attendance.Count(item => item.Attendance == SeatStatus.NoShow), attendance.Count(item => item.Attendance is null),
            waits.Length, waits.Count(item => item.Offered), waits.Count(item => item.Converted));
    }
}

using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class SlotDiagnostics
{
    private const int AlternativeLimit = 6;

    internal sealed record Reason(DateTimeOffset? StartsAt, string Code, string Message, string? Detail = null);
    internal sealed record Choice(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
    internal sealed record View(Reason[] Reasons, Choice[] Alternatives, string? BookingMessage);

    public static async Task<IResult> Check(string resourceId, DateOnly? date, string? time, string? timeZone,
        DateTimeOffset? startsAt, int? durationMinutes, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Schedule.TryZone(timeZone, out var viewer))
            return BookingEndpoints.InvalidTimeZone();

        var now = clock.GetUtcNow();
        var today = Schedule.LocalDate(now, viewer);
        var first = Schedule.Monday(today);
        var last = Schedule.Monday(Schedule.DisplayLastDay(now, viewer)).AddDays(6);
        DateTimeOffset[] candidates;
        DateTime local;
        if (startsAt is { } exact && date is null && time is null && InCalendar(Schedule.LocalDate(exact, viewer)))
        {
            candidates = [exact.ToUniversalTime()];
            local = TimeZoneInfo.ConvertTime(exact, viewer).DateTime;
        }
        else if (startsAt is null && date is { } day && InCalendar(day) && Schedule.TryTime(time, out var selectedTime))
        {
            candidates = Schedule.Instants(day, selectedTime, viewer);
            local = day.ToDateTime(selectedTime);
        }
        else
            return Results.Problem(statusCode: 400, title: "Выберите дату и время в пределах календаря.");

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resource = await scope.Resources(db).AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == resourceId && !item.IsArchived, ct);
        if (resource is null)
            return Results.NotFound();
        var schedule = Schedule.Read(resource);
        var duration = durationMinutes ?? Schedule.DefaultDuration;
        if (!schedule.Rules.Allows(duration))
            return Results.Problem(statusCode: 400, title: "Для ресурса недоступна эта длительность.");

        var owner = DemoAccounts.OwnerId(user);
        var atLimit = await schedule.Policies.AtLimit(db, resourceId, owner, now, ct);
        var external = await ExternalAvailability.Read(db, resourceId, now, ct);
        Reason[] reasons;
        if (candidates.Length == 0)
        {
            reasons = [new Reason(null, "clock-change", "Этого местного времени нет из-за перевода часов.")];
        }
        else
        {
            var windows = candidates.Select(start => schedule.Rules.Window(start, duration)).ToArray();
            var from = windows.Min(window => window.OccupiedFrom);
            var until = windows.Max(window => window.OccupiedUntil);
            var active = await db.Bookings.AsNoTracking()
                .Where(item => item.ResourceId == resourceId && item.CancelledAt == null
                    && item.OccupiedFromUtc < until && item.OccupiedUntilUtc > from)
                .OrderBy(item => item.StartUtc).ToArrayAsync(ct);
            reasons = windows.Select(window => Explain(window, schedule, duration, now, owner, scope.IsAdmin, atLimit, active, external)).ToArray();
        }

        Choice[] alternatives = [];
        if (!atLimit)
        {
            var open = await AvailabilitySearch.OpenStarts(db, resourceId, schedule, duration, now, ct);
            alternatives = open.Where(start => !candidates.Contains(start))
                .OrderBy(start => candidates.Length == 0
                    ? (TimeZoneInfo.ConvertTime(start, viewer).DateTime - local).Duration()
                    : candidates.Min(candidate => (start - candidate).Duration()))
                .ThenByDescending(start => start).Take(AlternativeLimit)
                .Select(start => new Choice(start, start.AddMinutes(duration))).ToArray();
        }
        await snapshot.CommitAsync(ct);
        return Results.Ok(new View(reasons, alternatives, atLimit ? schedule.Policies.LimitMessage : null));

        bool InCalendar(DateOnly day) => day >= first && day <= last;
    }

    private static Reason Explain(BookingWindow window, ResourceSchedule schedule, int duration, DateTimeOffset now,
        Guid owner, bool isAdmin, bool atLimit, Booking[] active, ExternalAvailability external)
    {
        var start = window.StartsAt;
        Reason Because(string code, string message, string? detail = null) => new(start, code, message, detail);
        if (start <= now)
            return Because("past", "Это время уже прошло.");
        if (!Schedule.Contains(start, schedule, duration))
            return ExplainSchedule(window, schedule);

        var occupied = active.FirstOrDefault(item => item.MaintenanceReason is not null && item.Window.Overlaps(window))
            ?? active.FirstOrDefault(item => item.Window.Overlaps(window));
        if (occupied is not null)
        {
            var actualOverlap = start < occupied.EndUtc && occupied.StartUtc < window.EndsAt;
            if (occupied.MaintenanceReason is { } maintenance)
                return actualOverlap
                    ? Because("maintenance", "На это время запланировано обслуживание.", maintenance)
                    : Because("buffer", "Буфер этой брони пересекается с обслуживанием.", maintenance);
            if (!actualOverlap)
                return Because("buffer", "Буфер между бронями занимает часть выбранного интервала.");
            if (occupied.SeatCapacity is not null)
                return Because("group", "На это время открыто групповое занятие.", occupied.SessionTitle);
            if (occupied.OwnerId == owner || isAdmin)
            {
                if (occupied.HoldExpiresAt is not null)
                    return Because("reserved", occupied.OwnerId == owner
                        ? "У вас есть временный резерв на это время." : "Время удерживает резерв из листа ожидания.");
                if (occupied.ApprovalStatus == BookingApproval.Pending)
                    return Because("pending", occupied.OwnerId == owner
                        ? "Ваша заявка на это время ожидает согласования." : "Время занято заявкой на согласовании.");
            }
            return occupied.OwnerId == owner
                ? Because("own-booking", "На это время у вас уже есть бронь.")
                : Because("occupied", "Это время занято.");
        }

        if (external.Conflict(window) is { } externalConflict)
            return Because(external.Stale ? "external-stale" : "external", externalConflict);
        if (window.OccupiedFrom <= now)
            return Because("preparation", "Для этого времени уже истёк срок на подготовку ресурса.");
        var policy = schedule.Policies;
        if (policy.TimeRestriction(start, now) is { } restriction)
            return Because(start >= policy.Horizon(now) ? "booking-window" : "advance-notice", restriction);
        if (atLimit)
            return Because("active-limit", policy.LimitMessage);
        return Because("available", "Это время доступно.");
    }

    private static Reason ExplainSchedule(BookingWindow window, ResourceSchedule schedule)
    {
        Reason Because(string code, string message) => new(window.StartsAt, code, message);
        var local = TimeZoneInfo.ConvertTime(window.StartsAt, schedule.Zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        var hours = schedule.HoursOn(day);
        if (hours.Hours.Length == 0)
            return schedule.Exceptions.Any(item => item.Date == day)
                ? Because("schedule-exception", "Ресурс закрыт по особому графику на эту дату.")
                : Because("closed-day", "В этот день ресурс закрыт по рабочему графику.");

        var minute = local.TimeOfDay.TotalMinutes;
        if (hours.Breaks.Any(pause => minute >= pause.From && minute < pause.To))
            return Because("break", "В это время у ресурса перерыв.");
        if (!hours.Hours.Any(period => minute >= period.From && minute < period.To))
            return Because("outside-hours", "Это время вне рабочих часов ресурса.");
        if (!Schedule.FitsHours(window, schedule))
            return Because("does-not-fit", "Выбранный интервал с буферами выходит за рабочие часы или пересекает перерыв.");
        return Because("off-grid", "На это время нет начала слота в графике ресурса.");
    }
}

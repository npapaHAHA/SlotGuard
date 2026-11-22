using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record WeeklySeriesInput(DateTimeOffset StartsAt, int Count, int? DurationMinutes = null,
    Dictionary<int, DateTimeOffset>? Replacements = null, Guid? SeriesId = null, string? Email = null,
    DateTimeOffset?[]? SeenStarts = null, BookingFormSubmission? BookingForm = null);
internal sealed record SeriesOccurrence(int Index, DateOnly Date, DateTimeOffset? StartsAt, string? Conflict,
    DateTimeOffset[] Alternatives);
internal sealed record SeriesPreview(Guid SeriesId, string ResourceName, string TimeZone, bool RequiresApproval, string? LimitMessage,
    SeriesOccurrence[] Occurrences);
internal sealed record CreatedSeries(Guid SeriesId, CreatedBooking[] Bookings);

internal sealed partial class SlotBookings
{
    private const int MaxSeriesCount = 8;

    public async Task<IResult> PreviewSeries(IQueryable<Resource> allowed, Guid owner, WeeklySeriesInput input, CancellationToken ct,
        bool guestManagement = false)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resource = await allowed.AsNoTracking().SingleOrDefaultAsync(item => !item.IsArchived, ct);
        if (resource is null)
            return Results.NotFound();
        if (resource.PriceMinor.HasValue)
            return BookingPayments.OnlyPublic();
        var now = clock.GetUtcNow();
        var schedule = Schedule.Read(resource);
        var duration = input.DurationMinutes ?? Schedule.DefaultDuration;
        if (PlanDates(input, schedule, duration, now) is not { } dates)
            return InvalidSeries();
        var seriesId = input.SeriesId is { } existingId && existingId != Guid.Empty ? existingId : Guid.NewGuid();
        var preview = await EvaluateSeries(seriesId, resource, schedule, duration, dates, owner, now, ct);
        if (guestManagement && await GuestBookingLimits.Reached(db, resource.Id, owner, dates.Length, now, ct))
            preview = preview with { LimitMessage = GuestBookingLimits.Message };
        await snapshot.CommitAsync(ct);
        return Results.Ok(preview);
    }

    public async Task<IResult> BookSeries(IQueryable<Resource> allowed, Guid owner, WeeklySeriesInput input,
        CancellationToken ct, bool guestManagement = false, string? guestEmail = null, PrivateLinkAccess? privateAccess = null)
    {
        if (input.SeriesId is not { } seriesId || seriesId == Guid.Empty)
            return InvalidSeries();
        var id = await allowed.Where(item => !item.IsArchived).Select(item => item.Id).SingleOrDefaultAsync(ct);
        if (id is null)
            return Results.NotFound();
        return await InResource(id, async (now, resource, schedule) =>
        {
            if (resource.IsArchived || !await allowed.AnyAsync(item => item.Id == id, ct))
                return Results.NotFound();
            if (resource.PriceMinor.HasValue)
                return BookingPayments.OnlyPublic();
            var duration = input.DurationMinutes ?? Schedule.DefaultDuration;
            if (PlanDates(input, schedule, duration, now) is not { } dates)
                return InvalidSeries();

            var privateId = privateAccess?.Id;
            var existing = await db.Bookings.AsNoTracking()
                .Where(item => item.ResourceId == id && item.OwnerId == owner && item.SeriesId == seriesId
                    && item.RescheduledFromId == null && item.PrivateLinkId == privateId).OrderBy(item => item.StartUtc).ToArrayAsync(ct);
            if (existing.Length > 0)
            {
                return existing.Length == input.Count && existing.All(item => BookingDuration(item) == duration)
                    && existing.Select(item => (DateTimeOffset?)item.StartUtc)
                        .SequenceEqual(input.SeenStarts ?? dates.Select(date => date.Start).ToArray())
                    ? Results.Ok(new CreatedSeries(seriesId, existing.Select(item => Receipt(item, schedule, now)).ToArray()))
                    : Problem(409, "Эта серия уже сохранена. Откройте свои записи.");
            }

            PrivateBookingLink? invitation = null;
            if (privateAccess is not null)
            {
                invitation = await db.PrivateBookingLinks.SingleOrDefaultAsync(item => item.Id == privateAccess.Id && item.ResourceId == id, ct);
                if (invitation is null)
                    return Results.NotFound();
                if (invitation.Check(now, privateAccess.Email, input.Count) is { } closed)
                    return closed;
            }
            var preview = await EvaluateSeries(seriesId, resource, schedule, duration, dates, owner, now, ct);
            if (guestManagement && await GuestBookingLimits.Reached(db, resource.Id, owner, dates.Length, now, ct))
                preview = preview with { LimitMessage = GuestBookingLimits.Message };
            if (preview.LimitMessage is not null || preview.Occurrences.Any(item => item.Conflict is not null)
                || input.SeenStarts is { } seen && !seen.SequenceEqual(dates.Select(date => date.Start)))
                return Results.Json(preview, statusCode: StatusCodes.Status409Conflict);
            if (BookingForm.Read(resource).Check(input.BookingForm, out var answers) is { } invalidAnswers)
                return invalidAnswers;

            var receipts = new List<CreatedBooking>(dates.Length);
            var contact = guestEmail ?? await ConfirmedEmail(owner, ct);
            foreach (var date in dates)
            {
                var booking = NewBooking(id, schedule.Rules.Window(date.Start!.Value, duration), owner,
                    guestEmail: contact, guestManagement: guestManagement);
                booking.SeriesId = seriesId;
                booking.PrivateLinkId = invitation?.Id;
                booking.AnswersJson = answers;
                if (resource.RequiresApproval)
                    booking.ApprovalStatus = BookingApproval.Pending;
                db.Bookings.Add(booking);
                BookingEmailQueue.Enqueue(db, booking, now);
                await WebhookEvents.Enqueue(db, resource.OrganizationId, booking, WebhookEvents.Created, now, ct);
                receipts.Add(Receipt(booking, schedule, now));
            }
            if (invitation is not null)
                invitation.UsedCount += receipts.Count;
            return Results.Json(new CreatedSeries(seriesId, receipts.ToArray()), statusCode: StatusCodes.Status201Created);
        }, ct);
    }

    private async Task<SeriesPreview> EvaluateSeries(Guid seriesId, Resource resource, ResourceSchedule schedule, int duration,
        PlannedDate[] dates, Guid owner, DateTimeOffset now, CancellationToken ct)
    {
        var zone = schedule.Zone;
        var horizon = schedule.Policies.Horizon(now).AddHours(3);
        var external = await ExternalAvailability.Read(db, resource.Id, now, ct);
        var busy = (await Active(resource.Id).AsNoTracking()
            .Where(item => item.OccupiedUntilUtc > now && item.OccupiedFromUtc < horizon).ToArrayAsync(ct))
            .Select(item => (item.Window, item.MaintenanceReason, item.HoldExpiresAt)).ToArray();
        var quota = schedule.Policies.MaxActiveBookings;
        string? limitMessage = null;
        if (quota is { } limit)
        {
            var existing = await BookingPolicy.ActiveForLimit(db, now)
                .CountAsync(item => item.ResourceId == resource.Id && item.OwnerId == owner, ct);
            if (limit - existing < dates.Length)
                limitMessage = $"Лимит на ресурс — {limit}. Можно добавить ещё {Math.Max(0, limit - existing)}.";
        }

        var windows = dates.Select(date => date.Start is { } start && Schedule.CanBook(start, now, schedule, duration)
            ? schedule.Rules.Window(start, duration) : (BookingWindow?)null).ToArray();
        ILookup<DateOnly, DateTimeOffset>? availableByWeek = null;
        var occurrences = new SeriesOccurrence[dates.Length];
        for (var index = 0; index < dates.Length; index++)
        {
            var planned = dates[index];
            var conflict = Conflict(planned, windows[index], index);
            DateTimeOffset[] alternatives = [];
            if (conflict is not null)
            {
                availableByWeek ??= Schedule.FutureStarts(now, schedule, duration)
                    .Where(start => IsFree(schedule.Rules.Window(start, duration))
                        && external.Conflict(schedule.Rules.Window(start, duration)) is null)
                    .ToLookup(start => Schedule.Monday(Schedule.LocalDate(start, zone)));
                alternatives = availableByWeek[Schedule.Monday(planned.Day)]
                    .Where(start => start != planned.Start && NoSeriesOverlap(schedule.Rules.Window(start, duration), index))
                    .OrderBy(start => (TimeZoneInfo.ConvertTime(start, zone).DateTime - planned.Local).Duration())
                    .ThenBy(start => start).Take(5).ToArray();
            }
            occurrences[index] = new SeriesOccurrence(index, planned.Day, planned.Start, conflict, alternatives);
        }
        return new SeriesPreview(seriesId, resource.Name, schedule.ZoneId, resource.RequiresApproval, limitMessage, occurrences);

        bool IsFree(BookingWindow window) => !busy.Any(item => item.Window.Overlaps(window));

        bool NoSeriesOverlap(BookingWindow window, int index) =>
            !windows.Where((_, other) => other != index).Any(other => other is { } candidate && candidate.Overlaps(window));

        string? Conflict(PlannedDate planned, BookingWindow? window, int index)
        {
            if (planned.Start is not { } start)
                return "Этого местного времени нет из-за перевода часов.";
            if (start <= now)
                return "Это время уже прошло.";
            if (schedule.Policies.TimeRestriction(start, now) is { } restriction)
                return restriction;
            if (window is null)
                return "Время не входит в график ресурса.";
            if (external.Conflict(window.Value) is { } externalConflict)
                return externalConflict;
            if (busy.Any(item => item.MaintenanceReason is not null && item.Window.Overlaps(window.Value)))
                return "Запланировано обслуживание.";
            if (busy.Any(item => item.HoldExpiresAt is not null && item.Window.Overlaps(window.Value)))
                return "Интервал временно зарезервирован.";
            if (!IsFree(window.Value))
                return "Интервал уже занят.";
            return NoSeriesOverlap(window.Value, index) ? null : "Интервал пересекается с другой датой серии.";
        }
    }

    private static PlannedDate[]? PlanDates(WeeklySeriesInput input, ResourceSchedule schedule, int duration, DateTimeOffset now)
    {
        if (input.Count is < 2 or > MaxSeriesCount || !schedule.Rules.Allows(duration)
            || !Schedule.InBookableHorizon(input.StartsAt, now)
            || input.Replacements?.Keys.Any(index => index < 0 || index >= input.Count) == true)
            return null;
        var zone = schedule.Zone;
        var anchor = TimeZoneInfo.ConvertTime(input.StartsAt, zone);
        var time = TimeOnly.FromDateTime(anchor.DateTime);
        var first = DateOnly.FromDateTime(anchor.DateTime);
        var dates = new PlannedDate[input.Count];
        for (var index = 0; index < dates.Length; index++)
        {
            var day = first.AddDays(7 * index);
            if (input.Replacements?.TryGetValue(index, out var replacement) == true)
            {
                var local = TimeZoneInfo.ConvertTime(replacement, zone).DateTime;
                if (Schedule.Monday(day) != Schedule.Monday(DateOnly.FromDateTime(local)))
                    return null;
                dates[index] = new PlannedDate(day, local, replacement.ToUniversalTime());
                continue;
            }

            var starts = Schedule.Instants(day, time, zone);
            var start = index == 0 ? input.StartsAt.ToUniversalTime() : starts
                .OrderByDescending(instant => zone.GetUtcOffset(instant) == anchor.Offset)
                .Select(instant => (DateTimeOffset?)instant).FirstOrDefault();
            dates[index] = new PlannedDate(day, day.ToDateTime(time), start);
        }
        return dates;
    }

    private static IResult InvalidSeries() =>
        Problem(400, "Выберите от 2 до 8 еженедельных записей. Замена должна быть в той же неделе.");

    private sealed record PlannedDate(DateOnly Day, DateTime Local, DateTimeOffset? Start);
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SlotGuard;

internal sealed class WaitlistOptions
{
    public int HoldSeconds { get; set; } = 600;
}

internal readonly record struct SlotKey(string ResourceId, DateTimeOffset StartsAt);
internal sealed record CreatedBooking(Guid Id, string ResourceId, DateTimeOffset StartsAt, string? ApprovalStatus,
    DateTimeOffset EndsAt, BookingChangeTimes? Changes = null, Guid? BundleId = null, BookingPaymentView? Payment = null);
internal sealed record JoinedWaitlist(long Id);
internal sealed record CreatedMaintenance(Guid Id, int Count);
internal sealed record RescheduleView(DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset[] Starts, bool RequiresApproval);
internal sealed record RescheduleInput(DateTimeOffset StartsAt);

internal sealed partial class SlotBookings(BookingDb db, TimeProvider clock, IOptions<WaitlistOptions> options, CalendarUpdates updates)
{
    public Task<IResult> Book(SlotKey slot, Guid owner, CancellationToken ct, Guid? publicLinkId = null,
        int duration = Schedule.DefaultDuration, string? guestEmail = null, BookingFormSubmission? submission = null,
        PrivateLinkAccess? privateAccess = null, RoutedBookingInput? routing = null, Guid? externalRequestId = null,
        EmbedScope? embed = null, IQueryable<Resource>? allowed = null, int? expectedPriceMinor = null) =>
        InResource(slot.ResourceId, async (now, resource, schedule) =>
        {
            if (allowed is not null && !await allowed.AnyAsync(item => item.Id == resource.Id, ct)
                || (embed is not null || publicLinkId.HasValue || privateAccess is not null)
                    && !await OrganizationStructure.AllowsLinks(db, resource.Id, ct)
                || embed is not null && !embed.Allows(resource)
                || publicLinkId.HasValue && (resource.IsArchived || resource.PublicLinkId != publicLinkId))
                return Results.NotFound();
            if (resource.IsArchived)
                return ArchivedResource();
            var hosted = publicLinkId.HasValue || privateAccess is not null;
            if (resource.PriceMinor.HasValue && !hosted)
                return BookingPayments.OnlyPublic();
            if (externalRequestId is { } requestId)
            {
                var previous = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == requestId, ct);
                if (previous is not null)
                    return previous.OwnerId == owner && previous.ResourceId == resource.Id && previous.StartUtc == slot.StartsAt
                        && BookingDuration(previous) == duration && previous.CancelledAt == null
                        ? Results.Ok(Receipt(previous, schedule, now))
                        : Problem(409, "Idempotency-Key уже использован для другой операции или бронь изменилась.");
            }
            if (!Schedule.InBookableHorizon(slot.StartsAt, now) || !schedule.Rules.Allows(duration))
                return InvalidSlot();
            var window = schedule.Rules.Window(slot.StartsAt, duration);
            var busy = await Overlapping(slot.ResourceId, window).ToListAsync(ct);
            var existing = busy.FirstOrDefault(item => item.StartUtc == window.StartsAt && item.EndUtc == window.EndsAt
                && item.OwnerId == owner && item.MaintenanceReason is null && item.SeatCapacity is null
                && (item.HoldExpiresAt is null || item.PriceMinor != null));
            if (hosted && existing is not null && existing.PrivateLinkId == privateAccess?.Id)
                return Results.Ok(Receipt(existing, schedule, now));
            if (hosted && expectedPriceMinor != resource.PriceMinor)
                return BookingPayments.QuoteChanged();
            PrivateBookingLink? invitation = null;
            if (privateAccess is not null)
            {
                invitation = await db.PrivateBookingLinks.SingleOrDefaultAsync(item =>
                    item.Id == privateAccess.Id && item.ResourceId == resource.Id, ct);
                if (invitation is null)
                    return Results.NotFound();
                if (invitation.Check(now, privateAccess.Email) is { } closed)
                    return closed;
            }
            RouteAssignment? route = null;
            if (routing is not null)
            {
                route = await BookingRouting.Resolve(db, resource.OrganizationId, routing.Answers!, owner, ct);
                if (route.Revision != routing.Revision)
                    return RoutingEndpoints.RuleChanged();
                if (!route.Matches(resource))
                    return Problem(409, "Ресурс больше не соответствует вашим ответам. Обновите варианты.");
            }
            if (ValidateStart(slot.StartsAt, now, schedule, duration) is { } invalid)
                return invalid;
            if (await ExternalConflict(slot.ResourceId, window, now, ct) is { } external)
                return external;
            if (busy.Count != 0)
                return Problem(409, "Этот интервал уже занят.");
            if (hosted && await GuestBookingLimits.Reached(db, resource.Id, owner, 1, now, ct))
                return PolicyConflict(GuestBookingLimits.Message);
            if (await schedule.Policies.AtLimit(db, resource.Id, owner, now, ct))
                return PolicyConflict(schedule.Policies.LimitMessage);
            if (BookingForm.Read(resource).Check(submission, out var answers) is { } invalidAnswers)
                return invalidAnswers;

            var booking = NewBooking(slot.ResourceId, window, owner, guestEmail: guestEmail,
                guestManagement: publicLinkId.HasValue || invitation is not null, bookingId: externalRequestId);
            booking.AnswersJson = answers;
            if (invitation is not null)
            {
                booking.PrivateLinkId = invitation.Id;
                invitation.UsedCount++;
            }
            booking.RoutingContext = route?.Context.Write();
            booking.RoutingReviewerId = route?.ReviewerId;
            if (resource.PriceMinor is { } amount)
            {
                booking.PriceMinor = amount;
                booking.HoldExpiresAt = new[] { now.AddMinutes(10), window.OccupiedFrom, invitation?.ExpiresAt ?? window.OccupiedFrom }.Min();
            }
            else if (resource.RequiresApproval || route?.RequiresReview == true)
                booking.ApprovalStatus = BookingApproval.Pending;
            db.Bookings.Add(booking);
            if (booking.PriceMinor is null)
            {
                await QueueConfirmation(booking, now, ct);
                await WebhookEvents.Enqueue(db, resource.OrganizationId, booking, WebhookEvents.Created, now, ct);
            }
            return Results.Json(Receipt(booking, schedule, now), statusCode: 201);
        }, ct, externalRequestId);

    public Task<IResult> JoinWaitlist(SlotKey slot, Guid owner, IQueryable<Resource> allowed, CancellationToken ct,
        int duration = Schedule.DefaultDuration) =>
        InResource(slot.ResourceId, async (now, resource, schedule) =>
        {
            if (!await allowed.AnyAsync(item => item.Id == resource.Id, ct))
                return Results.NotFound();
            if (resource.IsArchived)
                return ArchivedResource();
            if (resource.PriceMinor.HasValue)
                return BookingPayments.OnlyPublic();
            if (ValidateStart(slot.StartsAt, now, schedule, duration) is { } invalid)
                return invalid;
            var window = schedule.Rules.Window(slot.StartsAt, duration);
            if (await ExternalConflict(slot.ResourceId, window, now, ct) is { } external)
                return external;
            var busy = await Overlapping(slot.ResourceId, window).ToListAsync(ct);
            if (busy.Count == 0)
                return Problem(409, "Интервал свободен. Его можно забронировать.");
            if (busy.Any(item => item.MaintenanceReason is not null))
                return Problem(409, "На это время запланировано обслуживание.");
            if (busy.Any(item => item.OwnerId == owner))
                return Problem(409, "Часть интервала уже закреплена за вами.");

            var existing = await db.WaitlistEntries.SingleOrDefaultAsync(item => item.ResourceId == slot.ResourceId
                && item.StartUtc == slot.StartsAt && item.DurationMinutes == duration && item.OwnerId == owner
                && item.RemovedAt == null, ct);
            if (existing is not null)
                return Results.Ok(new JoinedWaitlist(existing.Id));
            if (await schedule.Policies.AtLimit(db, resource.Id, owner, now, ct))
                return PolicyConflict(schedule.Policies.LimitMessage);
            var entry = new WaitlistEntry
                { ResourceId = slot.ResourceId, StartUtc = slot.StartsAt, OwnerId = owner, DurationMinutes = duration };
            db.WaitlistEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            return Results.Json(new JoinedWaitlist(entry.Id), statusCode: 201);
        }, ct);

    public async Task<IResult> LeaveWaitlist(long id, Guid owner, IQueryable<Resource> allowed, CancellationToken ct)
    {
        var resources = allowed.Select(item => item.Id);
        var resourceId = await db.WaitlistEntries.AsNoTracking()
            .Where(entry => entry.Id == id && entry.OwnerId == owner && resources.Contains(entry.ResourceId))
            .Select(entry => entry.ResourceId).SingleOrDefaultAsync(ct);
        if (resourceId is null)
            return Problem(404, "Запись в листе ожидания не найдена.");

        return await InResource(resourceId, async (now, _, _) =>
        {
            if (!await resources.ContainsAsync(resourceId, ct))
                return Results.NotFound();
            var entry = await db.WaitlistEntries.SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, ct);
            if (entry is null)
                return Problem(404, "Запись в листе ожидания не найдена.");
            entry.RemovedAt = now;
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> Confirm(Guid id, Guid owner, IQueryable<Resource> allowed, CancellationToken ct,
        BookingFormSubmission? submission = null)
    {
        var resourceId = await db.Bookings.AsNoTracking()
            .Where(item => item.Id == id && item.OwnerId == owner && item.MaintenanceReason == null && item.SeatCapacity == null
                && allowed.Any(resource => resource.Id == item.ResourceId))
            .Select(item => item.ResourceId).SingleOrDefaultAsync(ct);
        if (resourceId is null)
            return Problem(404, "Резерв не найден.");

        return await InResource(resourceId, async (now, resource, _) =>
        {
            if (!await allowed.AnyAsync(item => item.Id == resourceId, ct))
                return Results.NotFound();
            var active = await ActiveBooking(id, ct);
            if (active is null || active.StartUtc <= now)
                return Problem(409, "Резерв больше недоступен.");
            if (active.PriceMinor is not null && active.PaidAt is null)
                return BookingPayments.OnlyPublic();

            if (active.HoldExpiresAt is not null)
            {
                if (await ExternalConflict(resourceId, active.Window, now, ct) is { } external)
                    return external;
                if (BookingForm.Read(resource).Check(submission, out var answers) is { } invalidAnswers)
                    return invalidAnswers;
                active.AnswersJson = answers;
                active.HoldExpiresAt = null;
                if (resource.RequiresApproval)
                    active.ApprovalStatus = BookingApproval.Pending;
                await QueueConfirmation(active, now, ct);
                await WebhookEvents.Enqueue(db, resource.OrganizationId, active, WebhookEvents.Created, now, ct);
            }
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> Decide(Guid id, IQueryable<Resource> allowed, IQueryable<Booking> reviewable,
        string decision, string reason, string actor, CancellationToken ct)
    {
        var target = await reviewable.AsNoTracking().Where(booking => booking.Id == id && booking.ApprovalStatus != null)
            .Select(booking => new { booking.ResourceId, booking.BundleId, booking.OwnerId }).SingleOrDefaultAsync(ct);
        if (target is null)
            return Problem(404, "Заявка не найдена.");
        if (target.BundleId is { } bundle)
            return await DecideBundle(id, bundle, target.OwnerId, allowed, decision, reason, actor, ct);
        var resourceId = target.ResourceId;

        return await InResource(resourceId, async (now, resource, schedule) =>
        {
            if (!await reviewable.AnyAsync(item => item.Id == id, ct))
                return Problem(404, "Заявка не найдена.");
            var booking = await ActiveBooking(id, ct);
            if (booking is null || booking.ApprovalStatus != BookingApproval.Pending || booking.StartUtc <= now)
                return Problem(409, "Заявка больше не ожидает решения.");
            if (decision == BookingApproval.Approved && await ExternalConflict(resourceId, booking.Window, now, ct) is { } external)
                return external;

            booking.ApprovalStatus = decision;
            booking.ApprovalReason = reason;
            Audit.Add(db, resource, actor, $"booking.{decision}", reason, now);
            if (decision == BookingApproval.Rejected)
                await Release([booking], resource, schedule, now, ct);
            else
            {
                await QueueConfirmation(booking, now, ct);
                await WebhookEvents.Enqueue(db, resource.OrganizationId, booking, WebhookEvents.Approved, now, ct);
            }
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> Cancel(Guid id, Guid owner, IQueryable<Resource> allowed, CancellationToken ct)
    {
        var target = await ScopedBooking(id, owner, allowed).Select(item => new
            { item.Booking.ResourceId, item.Booking.BundleId }).SingleOrDefaultAsync(ct);
        if (target is null)
            return Problem(404, "Активная бронь не найдена.");
        if (target.BundleId is { } bundle)
            return await CancelBundle(id, bundle, owner, allowed, ct);
        var resourceId = target.ResourceId;

        return await InResource(resourceId, async (now, resource, schedule) =>
        {
            if (resource.IsArchived || !await allowed.AnyAsync(item => item.Id == resourceId, ct))
                return Results.NotFound();
            var active = await ActiveBooking(id, ct);
            if (active is null || active.StartUtc <= now)
                return Problem(404, "Активная бронь не найдена.");
            var changes = schedule.Policies.Changes(active.StartUtc, now);
            if (active.HoldExpiresAt is null && !changes.CanCancel)
                return PolicyConflict("Срок отмены брони истёк.", changes.CancelUntil);

            await Release([active], resource, schedule, now, ct);
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> RescheduleOptions(Guid id, Guid owner, IQueryable<Resource> allowed, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await ScopedBooking(id, owner, allowed).AsNoTracking().SingleOrDefaultAsync(ct);
        if (current is null)
            return Results.NotFound();
        var now = clock.GetUtcNow();
        if (!CanReschedule(current.Booking, now))
            return CannotReschedule();

        var booking = current.Booking;
        if (booking.BundleId is { } bundle)
            return await BundleOptions(booking, bundle, owner, allowed, now, ct);
        var schedule = Schedule.Read(current.Resource);
        var changes = schedule.Policies.Changes(booking.StartUtc, now);
        if (!changes.CanReschedule)
            return PolicyConflict("Срок переноса брони истёк.", changes.RescheduleUntil);
        var starts = await AvailabilitySearch.OpenStarts(db, booking.ResourceId, schedule, BookingDuration(booking), now, ct, id);
        await snapshot.CommitAsync(ct);
        return Results.Ok(new RescheduleView(booking.StartUtc, booking.EndUtc,
            starts.Where(start => start != booking.StartUtc).ToArray(), BookingRouting.NeedsReview(booking, current.Resource)));
    }

    public async Task<IResult> Reschedule(Guid id, Guid owner, IQueryable<Resource> allowed, DateTimeOffset requested,
        string actor, CancellationToken ct)
    {
        var target = await ScopedBooking(id, owner, allowed).Select(item => new
            { item.Booking.ResourceId, item.Booking.BundleId }).SingleOrDefaultAsync(ct);
        if (target is null)
            return Results.NotFound();
        if (target.BundleId is { } bundle)
            return await MoveBundle(id, bundle, owner, allowed, requested.ToUniversalTime(), actor, ct);
        var resourceId = target.ResourceId;

        var start = requested.ToUniversalTime();
        return await InResource(resourceId, async (now, resource, schedule) =>
        {
            // Publication or archive status may have changed while waiting for the resource lock.
            if (resource.IsArchived || !await allowed.AnyAsync(item => item.Id == resourceId, ct))
                return Results.NotFound();

            var replacement = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(item => item.RescheduledFromId == id, ct);
            if (replacement is not null)
            {
                return replacement.CancelledAt is null && replacement.StartUtc == start
                    ? Results.Ok(Receipt(replacement, schedule, now))
                    : Problem(409, "Эту бронь уже перенесли. Обновите список записей.");
            }

            var original = await ActiveBooking(id, ct);
            if (original is null || !CanReschedule(original, now))
                return CannotReschedule();
            if (original.StartUtc == start)
                return Results.Ok(Receipt(original, schedule, now));
            var changes = schedule.Policies.Changes(original.StartUtc, now);
            if (!changes.CanReschedule)
                return PolicyConflict("Срок переноса брони истёк.", changes.RescheduleUntil);
            var duration = BookingDuration(original);
            if (ValidateStart(start, now, schedule, duration) is { } invalid)
                return invalid;

            var target = schedule.Rules.Window(start, duration);
            if (await ExternalConflict(resourceId, target, now, ct) is { } external)
                return external;
            if (await Overlapping(resourceId, target).AnyAsync(item => item.Id != id, ct))
                return Problem(409, "Новое время уже заняли. Исходная бронь сохранена.");

            // A move may overlap its own old interval. Retire that row before inserting the new one,
            // then offer the released time only after the replacement is visible within the transaction.
            original.CancelledAt = now;
            await db.SaveChangesAsync(ct);
            if (original.ContactEmail is not null)
                await BookingEmailQueue.Suppress(db, [original.Id], now, ct);
            var moved = NewBooking(resourceId, target, owner, original);
            if (BookingRouting.NeedsReview(original, resource))
                moved.ApprovalStatus = BookingApproval.Pending;
            db.Bookings.Add(moved);
            await QueueConfirmation(moved, now, ct);
            await WebhookEvents.Enqueue(db, resource.OrganizationId, moved, WebhookEvents.Rescheduled, now, ct);
            Audit.Add(db, resource, actor, "booking.rescheduled", $"{original.StartUtc:dd.MM.yyyy HH:mm} → {start:dd.MM.yyyy HH:mm} UTC", now);
            await db.SaveChangesAsync(ct);
            await OfferReleased([original.Window], resource, schedule, now, ct);
            return Results.Json(Receipt(moved, schedule, now), statusCode: 201);
        }, ct);
    }

    internal static bool CanReschedule(Booking booking, DateTimeOffset now) =>
        booking.CancelledAt is null && booking.HoldExpiresAt is null && booking.StartUtc > now;

    private static int BookingDuration(Booking booking) => (int)(booking.EndUtc - booking.StartUtc).TotalMinutes;

    private IQueryable<BookingAndResource> ScopedBooking(Guid id, Guid owner, IQueryable<Resource> allowed) =>
        from booking in db.Bookings
        join resource in allowed on booking.ResourceId equals resource.Id
        where booking.Id == id && booking.OwnerId == owner && booking.MaintenanceReason == null && booking.SeatCapacity == null && !resource.IsArchived
        select new BookingAndResource { Booking = booking, Resource = resource };

    private sealed class BookingAndResource
    {
        public required Booking Booking { get; init; }
        public required Resource Resource { get; init; }
    }

    private static IResult CannotReschedule() => Problem(409, "Перенос доступен для будущей активной брони.");

    public Task<IResult> BlockSlot(SlotKey slot, Guid admin, string actor, string reason, CancellationToken ct,
        int duration = Schedule.DefaultDuration) =>
        BlockWindows(slot.ResourceId, schedule => Schedule.Contains(slot.StartsAt, schedule, duration, forMaintenance: true)
            ? [BookingWindow.At(slot.StartsAt, duration)] : [], admin, actor, reason, ct);

    public Task<IResult> BlockWindow(string resourceId, DateOnly day, TimeOnly from, TimeOnly through, bool endExclusive,
        Guid admin, string actor, string reason, CancellationToken ct) =>
        BlockWindows(resourceId, schedule => SelectMaintenanceWindows(schedule, day, from, through, endExclusive),
            admin, actor, reason, ct);

    private static BookingWindow[] SelectMaintenanceWindows(ResourceSchedule schedule, DateOnly day, TimeOnly from,
        TimeOnly through, bool endExclusive)
    {
        if (endExclusive)
        {
            var starts = Schedule.Instants(day, from, schedule.Zone);
            var ends = Schedule.Instants(day, through, schedule.Zone);
            if (starts.Length != 1 || ends.Length != 1 || starts[0] >= ends[0])
                return [];
            var block = new BookingWindow(starts[0], ends[0], starts[0], ends[0]);
            return Schedule.FitsHours(block, schedule) ? [block] : [];
        }

        var legacy = schedule with { Booking = BookingRules.Standard };
        return Schedule.StartsOn(day, legacy).Where(start =>
        {
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, schedule.Zone).DateTime);
            return from <= local && local <= through;
        }).Select(start => BookingWindow.At(start, Schedule.DefaultDuration)).ToArray();
    }

    private Task<IResult> BlockWindows(string resourceId, Func<ResourceSchedule, BookingWindow[]> select,
        Guid admin, string actor, string reason, CancellationToken ct) =>
        InResource(resourceId, async (now, resource, schedule) =>
        {
            if (resource.IsArchived)
                return ArchivedResource();
            var requested = select(schedule);
            if (requested.Length == 0 || requested.Any(window => !Schedule.InBookableHorizon(window.StartsAt, now)))
                return InvalidSlot();

            var first = requested.Min(window => window.OccupiedFrom);
            var last = requested.Max(window => window.OccupiedUntil);
            var occupied = await Active(resourceId).AsNoTracking()
                .Where(item => item.OccupiedFromUtc < last && first < item.OccupiedUntilUtc).ToListAsync(ct);
            var conflicts = occupied.Where(item => requested.Any(window => window.Overlaps(item.Window)))
                .Select(item => item.StartUtc).Order().Distinct().ToArray();
            if (conflicts.Length > 0)
                return Results.Problem(statusCode: 409, title: "Интервал пересекается с активными записями.",
                    extensions: new Dictionary<string, object?> { ["conflicts"] = conflicts });

            var blocks = requested.Select(window => NewBooking(resourceId, window, admin)).ToArray();
            foreach (var block in blocks)
                block.MaintenanceReason = reason;
            db.Bookings.AddRange(blocks);
            Audit.Add(db, resource, actor, "maintenance.created", reason, now);
            return Results.Json(new CreatedMaintenance(blocks[0].Id, blocks.Length), statusCode: 201);
        }, ct);

    public async Task<IResult> Unblock(Guid id, string organizationId, string actor, CancellationToken ct)
    {
        var resources = ResourceIds(organizationId);
        var resourceId = await db.Bookings.AsNoTracking()
            .Where(item => item.Id == id && item.MaintenanceReason != null && resources.Contains(item.ResourceId))
            .Select(item => item.ResourceId).SingleOrDefaultAsync(ct);
        if (resourceId is null)
            return Problem(404, "Блокировка не найдена.");

        return await InResource(resourceId, async (now, resource, schedule) =>
        {
            var active = await ActiveBooking(id, ct);
            if (active is null || active.StartUtc <= now)
                return Problem(404, "Активная блокировка не найдена.");

            Audit.Add(db, resource, actor, "maintenance.removed", active.MaintenanceReason!, now);
            await Release([active], resource, schedule, now, ct);
            return Results.NoContent();
        }, ct);
    }

    public async Task ExpireHolds(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var resources = await db.Bookings.AsNoTracking()
            .Where(item => item.CancelledAt == null && item.HoldExpiresAt <= now)
            .OrderBy(item => item.HoldExpiresAt).Take(100).Select(item => item.ResourceId).ToListAsync(ct);
        foreach (var id in resources.Distinct())
            await InResource(id, (_, _, _) => Task.FromResult(Results.NoContent()), ct);
    }

    private Task<IResult> InResource(string id, Func<DateTimeOffset, Resource, ResourceSchedule, Task<IResult>> operation,
        CancellationToken ct, Guid? requestId = null) => InResources([id], (now, locked) =>
            operation(now, locked[0].Resource, locked[0].Schedule), ct, requestId);

    private sealed record LockedResource(Resource Resource, ResourceSchedule Schedule);

    private async Task<IResult> InResources(string[] ids, Func<DateTimeOffset, LockedResource[], Task<IResult>> operation,
        CancellationToken ct, Guid? requestId = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (requestId is { } request)
        {
            var key = BitConverter.ToInt64(request.ToByteArray());
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        }

        // Every operation spanning multiple resources acquires the same row locks in the same order.
        var locked = new List<LockedResource>(ids.Length);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var resource = await ResourceBookingLock.Acquire(db, id, ct);
            locked.Add(new LockedResource(resource, Schedule.Read(resource)));
        }

        var now = clock.GetUtcNow();
        var released = false;
        try
        {
            foreach (var item in locked)
            {
                var expired = await Active(item.Resource.Id).Where(booking => booking.HoldExpiresAt <= now).ToArrayAsync(ct);
                if (expired.Length == 0)
                    continue;
                await Release(expired, item.Resource, item.Schedule, now, ct);
                await db.SaveChangesAsync(ct);
                released = true;
            }

            var result = await operation(now, locked.ToArray());
            var changed = await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (changed != 0 || released || result is IStatusCodeHttpResult { StatusCode: 201 or 204 })
                await updates.Publish(locked[0].Resource.OrganizationId);
            return result;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { ConstraintName: BookingDb.ActiveWindowConstraint or BookingDb.ActiveSlotIndex })
        {
            await transaction.RollbackAsync(ct);
            return Problem(409, "Этот интервал уже занят.");
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task Release(Booking[] released, Resource resource, ResourceSchedule schedule, DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (var item in released)
        {
            item.CancelledAt = now;
            if (item.PriceMinor is { } amount)
            {
                if (item.PaidAt is null)
                {
                    if (item.PrivateLinkId is { } id)
                    {
                        var link = await db.PrivateBookingLinks.SingleAsync(row => row.Id == id, ct);
                        link.UsedCount--;
                    }
                    continue;
                }
                item.PaymentRefundedAt = now;
                Audit.Add(db, resource, "Демо-касса", "payment.refunded", BookingPayments.Format(amount), now);
            }
            await WebhookEvents.Enqueue(db, resource.OrganizationId, item, WebhookEvents.Cancelled, now, ct);
        }
        // The exclusion constraint checks each write immediately.
        await db.SaveChangesAsync(ct);
        await BookingEmailQueue.Suppress(db, released.Where(item => item.ContactEmail is not null)
            .Select(item => item.Id).ToArray(), now, ct);
        await OfferReleased(released.Select(item => item.Window).ToArray(), resource, schedule, now, ct);
    }

    private async Task OfferReleased(BookingWindow[] released, Resource resource, ResourceSchedule schedule,
        DateTimeOffset now, CancellationToken ct)
    {
        if (resource.PriceMinor.HasValue)
            return;
        var first = released.Min(item => item.OccupiedFrom);
        var last = released.Max(item => item.OccupiedUntil);
        var earliestStart = first.AddMinutes(-150); // The longest request plus its after-buffer.
        var latestStart = last.AddMinutes(60);
        var requests = await db.WaitlistEntries
            .Where(item => item.ResourceId == resource.Id && item.RemovedAt == null
                && item.StartUtc > now && item.StartUtc > earliestStart && item.StartUtc < latestStart)
            .OrderBy(item => item.Id).ToListAsync(ct);
        if (requests.Count == 0)
            return;

        var candidates = requests.Where(item => Schedule.CanBook(item.StartUtc, now, schedule, item.DurationMinutes))
            .Select(item => (Entry: item, Window: schedule.Rules.Window(item.StartUtc, item.DurationMinutes)))
            .Where(item => released.Any(old => old.Overlaps(item.Window))).ToArray();
        if (candidates.Length == 0)
            return;

        var external = await ExternalAvailability.Read(db, resource.Id, now, ct);
        var from = candidates.Min(item => item.Window.OccupiedFrom);
        var until = candidates.Max(item => item.Window.OccupiedUntil);
        var busy = (await Active(resource.Id).AsNoTracking()
            .Where(item => item.OccupiedFromUtc < until && from < item.OccupiedUntilUtc).ToListAsync(ct))
            .Select(item => item.Window).ToList();
        var quota = schedule.Policies.MaxActiveBookings;
        var restricted = resource.GroupId.HasValue && !await OrganizationStructure.AllowsLinks(db, resource.Id, ct);
        Dictionary<Guid, int> used = [];
        if (quota.HasValue)
        {
            var owners = candidates.Select(item => item.Entry.OwnerId).Distinct().ToArray();
            used = await BookingPolicy.ActiveForLimit(db, now)
                .Where(item => item.ResourceId == resource.Id && owners.Contains(item.OwnerId))
                .GroupBy(item => item.OwnerId).Select(group => new { Owner = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Owner, item => item.Count, ct);
        }
        foreach (var (entry, window) in candidates)
        {
            if (busy.Any(item => item.Overlaps(window)) || external.Conflict(window) is not null)
                continue;
            entry.RemovedAt = now;
            if (restricted && !await OrganizationStructure.Visible(db, db.Resources, entry.OwnerId)
                    .AnyAsync(item => item.Id == resource.Id, ct))
                continue;
            if (quota is { } max)
            {
                var count = used.GetValueOrDefault(entry.OwnerId);
                if (count >= max)
                    continue;
                used[entry.OwnerId] = count + 1;
            }
            var hold = NewBooking(resource.Id, window, entry.OwnerId);
            var deadline = now.AddSeconds(options.Value.HoldSeconds);
            hold.HoldExpiresAt = deadline < entry.StartUtc ? deadline : entry.StartUtc;
            entry.OfferedBookingId = hold.Id;
            db.Bookings.Add(hold);
            db.HoldOutbox.Add(new HoldOutboxEvent
            {
                Id = Guid.NewGuid(), BookingId = hold.Id, CreatedAt = now, NextAttemptAt = now
            });
            busy.Add(window);
        }
    }

    public async Task ReevaluateWaitlist(string resourceId, CancellationToken ct) =>
        await InResource(resourceId, async (now, resource, schedule) =>
        {
            if (!resource.IsArchived)
            {
                var until = Schedule.MaximumHorizon(now).AddHours(3);
                await OfferReleased([new BookingWindow(now, until, now, until)], resource, schedule, now, ct);
            }
            return Results.Ok();
        }, ct);

    private async Task<IResult?> ExternalConflict(string resourceId, BookingWindow window, DateTimeOffset now, CancellationToken ct)
    {
        var availability = await ExternalAvailability.Read(db, resourceId, now, ct);
        return availability.Conflict(window) is { } reason ? Problem(availability.Stale ? 422 : 409, reason) : null;
    }

    private IQueryable<Booking> Active(string resourceId) =>
        db.Bookings.Where(item => item.ResourceId == resourceId && item.CancelledAt == null);

    private IQueryable<Booking> Overlapping(string resourceId, BookingWindow window) => Active(resourceId)
        .Where(item => item.OccupiedFromUtc < window.OccupiedUntil && window.OccupiedFrom < item.OccupiedUntilUtc);

    private Task<Booking?> ActiveBooking(Guid id, CancellationToken ct) =>
        db.Bookings.SingleOrDefaultAsync(item => item.Id == id && item.CancelledAt == null, ct);

    private IQueryable<string> ResourceIds(string organizationId) =>
        db.Resources.Where(item => item.OrganizationId == organizationId).Select(item => item.Id);

    private async Task QueueConfirmation(Booking booking, DateTimeOffset now, CancellationToken ct)
    {
        if (!BookingCalendar.IsConfirmed(booking))
            return;
        booking.ContactEmail ??= await ConfirmedEmail(booking.OwnerId, ct);
        BookingEmailQueue.Enqueue(db, booking, now);
    }

    private Task<string?> ConfirmedEmail(Guid owner, CancellationToken ct) => db.Users.AsNoTracking()
        .Where(account => account.Id == owner && account.EmailConfirmed)
        .Select(account => account.Email).SingleOrDefaultAsync(ct);

    private static Booking NewBooking(string id, BookingWindow window, Guid owner, Booking? previous = null,
        string? guestEmail = null, bool guestManagement = false, Guid? bookingId = null, Guid? bundleId = null) => new()
    {
        Id = bookingId ?? Guid.NewGuid(), OwnerId = owner, ResourceId = id, StartUtc = window.StartsAt, EndUtc = window.EndsAt,
        OccupiedFromUtc = window.OccupiedFrom, OccupiedUntilUtc = window.OccupiedUntil, RescheduledFromId = previous?.Id,
        CalendarRootId = previous?.CalendarRootId ?? previous?.Id, CalendarSequence = previous is null ? 0 : previous.CalendarSequence + 1,
        ContactEmail = previous?.ContactEmail ?? guestEmail, GuestManagement = previous?.GuestManagement ?? guestManagement,
        SeriesId = previous?.SeriesId, BundleId = previous?.BundleId ?? bundleId, AnswersJson = previous?.AnswersJson,
        PriceMinor = previous?.PriceMinor, PaidAt = previous?.PaidAt,
        PrivateLinkId = previous?.PrivateLinkId, RoutingContext = previous?.RoutingContext, RoutingReviewerId = previous?.RoutingReviewerId
    };

    private static CreatedBooking Receipt(Booking item, ResourceSchedule schedule, DateTimeOffset now) =>
        new(item.Id, item.ResourceId, item.StartUtc, item.ApprovalStatus, item.EndUtc,
            schedule.Policies.Changes(item.StartUtc, now), item.BundleId, BookingPayments.Describe(item, now));

    private static IResult? ValidateStart(DateTimeOffset start, DateTimeOffset now, ResourceSchedule schedule, int duration)
    {
        if (!Schedule.InBookableHorizon(start, now))
            return InvalidSlot();
        if (schedule.Policies.TimeRestriction(start, now) is { } reason)
            return PolicyConflict(reason);
        return Schedule.CanBook(start, now, schedule, duration) ? null : InvalidSlot();
    }

    private static IResult PolicyConflict(string title, DateTimeOffset? deadline = null) => Results.Problem(
        statusCode: 422, title: title, extensions: deadline.HasValue ? new Dictionary<string, object?> { ["deadline"] = deadline } : null);

    private static IResult InvalidSlot() => Problem(400, "Выберите будущий интервал из календаря с доступной длительностью.");
    private static IResult ArchivedResource() => Problem(409, "Этот ресурс находится в архиве.");
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}

internal static class ResourceBookingLock
{
    public static async Task<Resource> Acquire(BookingDb db, string id, CancellationToken ct) =>
        (await db.Resources.FromSqlInterpolated($"""
            SELECT * FROM "Resources" WHERE "Id" = {id} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct)).Single();
}

using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed partial class SlotBookings
{
    private IQueryable<Booking> Bundle(Guid bundle, Guid owner, IQueryable<Resource> allowed) =>
        db.Bookings.Where(item => item.BundleId == bundle && item.OwnerId == owner
            && allowed.Any(resource => resource.Id == item.ResourceId && !resource.IsArchived));

    private async Task<IResult> InBundle(Guid id, Guid bundle, Guid owner, IQueryable<Resource> allowed,
        Func<DateTimeOffset, BundleItem[], Task<IResult>> operation, CancellationToken ct)
    {
        var query = Bundle(bundle, owner, allowed);
        var ids = await query.Select(item => item.ResourceId).Distinct().ToArrayAsync(ct);
        if (ids.Length < 2)
            return Results.NotFound();

        return await InResources(ids, async (now, locked) =>
        {
            var bookings = await query.Where(item => item.CancelledAt == null).ToArrayAsync(ct);
            if (bookings.Length != ids.Length || !bookings.Any(item => item.Id == id))
                return Problem(409, "Комплект уже изменился. Обновите список записей.");

            var items = locked.Select(item =>
                new BundleItem(bookings.Single(booking => booking.ResourceId == item.Resource.Id), item.Resource, item.Schedule))
                .ToArray();
            return await operation(now, items);
        }, ct);
    }

    private sealed record BundleItem(Booking Booking, Resource Resource, ResourceSchedule Schedule);

    private Task<IResult> CancelBundle(Guid id, Guid bundle, Guid owner, IQueryable<Resource> allowed, CancellationToken ct) =>
        InBundle(id, bundle, owner, allowed, async (now, items) =>
        {
            if (items.Any(item => item.Booking.StartUtc <= now))
                return Problem(404, "Активная бронь не найдена.");
            var changes = BundleChangeTimes(items.Select(item => item.Schedule.Policies.Changes(item.Booking.StartUtc, now)));
            if (!changes.CanCancel)
                return PolicyConflict("Срок отмены комплекта истёк.", changes.CancelUntil);

            foreach (var item in items)
                await Release([item.Booking], item.Resource, item.Schedule, now, ct);
            return Results.NoContent();
        }, ct);

    private Task<IResult> DecideBundle(Guid id, Guid bundle, Guid owner, IQueryable<Resource> allowed, string decision,
        string reason, string actor, CancellationToken ct) =>
        InBundle(id, bundle, owner, allowed, async (now, items) =>
        {
            if (items.Any(item => item.Booking.ApprovalStatus != BookingApproval.Pending || item.Booking.StartUtc <= now))
                return Problem(409, "Комплект больше не ожидает решения.");
            if (decision == BookingApproval.Approved)
            {
                foreach (var item in items)
                    if (await ExternalConflict(item.Resource.Id, item.Booking.Window, now, ct) is { } external)
                        return external;
            }

            foreach (var item in items)
            {
                item.Booking.ApprovalStatus = decision;
                item.Booking.ApprovalReason = reason;
                Audit.Add(db, item.Resource, actor, $"booking.{decision}", reason, now);
                if (decision == BookingApproval.Rejected)
                    await Release([item.Booking], item.Resource, item.Schedule, now, ct);
                else
                {
                    await QueueConfirmation(item.Booking, now, ct);
                    await WebhookEvents.Enqueue(db, item.Resource.OrganizationId, item.Booking, WebhookEvents.Approved, now, ct);
                }
            }
            return Results.NoContent();
        }, ct);

    private async Task<IResult> BundleOptions(Booking current, Guid bundle, Guid owner, IQueryable<Resource> allowed,
        DateTimeOffset now, CancellationToken ct)
    {
        var bookings = await (from booking in Bundle(bundle, owner, allowed).AsNoTracking()
            join resource in allowed on booking.ResourceId equals resource.Id
            where booking.CancelledAt == null
            select new BookingAndResource { Booking = booking, Resource = resource }).ToArrayAsync(ct);
        if (bookings.Length < 2 || bookings.Any(item => !CanReschedule(item.Booking, now)))
            return CannotReschedule();

        var items = bookings.Select(item => new BundleItem(item.Booking, item.Resource, Schedule.Read(item.Resource))).ToArray();
        var changes = BundleChangeTimes(items.Select(item => item.Schedule.Policies.Changes(item.Booking.StartUtc, now)));
        if (!changes.CanReschedule)
            return PolicyConflict("Срок переноса комплекта истёк.", changes.RescheduleUntil);
        var starts = new HashSet<DateTimeOffset>();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var available = await AvailabilitySearch.OpenStarts(db, item.Resource.Id, item.Schedule, BookingDuration(current), now,
                ct, item.Booking.Id);
            if (index == 0)
                starts.UnionWith(available);
            else
                starts.IntersectWith(available);
            if (starts.Count == 0)
                break;
        }
        return Results.Ok(new RescheduleView(current.StartUtc, current.EndUtc,
            starts.Where(start => start != current.StartUtc).Order().ToArray(), items.Any(item => item.Resource.RequiresApproval)));
    }

    private async Task<IResult> MoveBundle(Guid id, Guid bundle, Guid owner, IQueryable<Resource> allowed, DateTimeOffset start,
        string actor, CancellationToken ct)
    {
        var result = await InBundle(id, bundle, owner, allowed, async (now, items) =>
        {
            if (items.Any(item => !CanReschedule(item.Booking, now)))
                return CannotReschedule();
            var original = items.Single(item => item.Booking.Id == id);
            if (original.Booking.StartUtc == start)
                return Results.Ok(Receipt(original.Booking, original.Schedule, now));

            var changes = BundleChangeTimes(items.Select(item => item.Schedule.Policies.Changes(item.Booking.StartUtc, now)));
            if (!changes.CanReschedule)
                return PolicyConflict("Срок переноса комплекта истёк.", changes.RescheduleUntil);
            var duration = BookingDuration(original.Booking);
            var windows = new Dictionary<string, BookingWindow>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (ValidateStart(start, now, item.Schedule, duration) is { } invalid)
                    return invalid;
                var target = item.Schedule.Rules.Window(start, duration);
                if (await ExternalConflict(item.Resource.Id, target, now, ct) is { } external)
                    return external;
                if (await Overlapping(item.Resource.Id, target).AnyAsync(booking => booking.Id != item.Booking.Id, ct))
                    return Problem(409, "Новое время уже заняли. Исходный комплект сохранён.");
                windows.Add(item.Resource.Id, target);
            }

            foreach (var item in items)
                item.Booking.CancelledAt = now;
            await db.SaveChangesAsync(ct);
            await BookingEmailQueue.Suppress(db, items.Where(item => item.Booking.ContactEmail is not null)
                .Select(item => item.Booking.Id).ToArray(), now, ct);
            var needsApproval = items.Any(item => item.Resource.RequiresApproval);
            Booking? moved = null;
            foreach (var item in items)
            {
                var replacement = NewBooking(item.Resource.Id, windows[item.Resource.Id], owner, item.Booking);
                if (needsApproval)
                    replacement.ApprovalStatus = BookingApproval.Pending;
                db.Bookings.Add(replacement);
                await QueueConfirmation(replacement, now, ct);
                await WebhookEvents.Enqueue(db, item.Resource.OrganizationId, replacement, WebhookEvents.Rescheduled, now, ct);
                Audit.Add(db, item.Resource, actor, "booking.rescheduled",
                    $"{item.Booking.StartUtc:dd.MM.yyyy HH:mm} → {start:dd.MM.yyyy HH:mm} UTC", now);
                if (item.Booking.Id == id)
                    moved = replacement;
            }
            await db.SaveChangesAsync(ct);
            foreach (var item in items)
                await OfferReleased([item.Booking.Window], item.Resource, item.Schedule, now, ct);
            return Results.Json(Receipt(moved!, original.Schedule, now), statusCode: 201);
        }, ct);

        if (result is IStatusCodeHttpResult { StatusCode: 409 })
        {
            var replacement = await Bundle(bundle, owner, allowed).AsNoTracking()
                .Where(item => item.RescheduledFromId == id && item.CancelledAt == null && item.StartUtc == start)
                .SingleOrDefaultAsync(ct);
            if (replacement is not null)
            {
                var resource = await allowed.AsNoTracking().SingleAsync(item => item.Id == replacement.ResourceId, ct);
                return Results.Ok(Receipt(replacement, Schedule.Read(resource), clock.GetUtcNow()));
            }
        }
        return result;
    }

    internal static BookingChangeTimes BundleChangeTimes(IEnumerable<BookingChangeTimes> items)
    {
        var changes = items.ToArray();
        return new BookingChangeTimes(changes.All(item => item.CanCancel), changes.All(item => item.CanReschedule),
            changes.Min(item => item.CancelUntil), changes.Min(item => item.RescheduleUntil));
    }
}

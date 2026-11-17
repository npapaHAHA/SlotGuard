using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class WaitlistOptions
{
    public int HoldSeconds { get; set; } = 600;
}

internal readonly record struct SlotKey(string ResourceId, DateTimeOffset StartsAt);
internal sealed record CreatedBooking(Guid Id, string ResourceId, DateTimeOffset StartsAt, string? ApprovalStatus = null);
internal sealed record JoinedWaitlist(long Id);
internal sealed record CreatedMaintenance(Guid Id, int Count);

internal sealed class SlotBookings(BookingDb db, TimeProvider clock, IOptions<WaitlistOptions> options, CalendarUpdates updates)
{
    private bool releasedHold;

    public Task<IResult> Book(SlotKey slot, Guid owner, CancellationToken ct) =>
        InSlot(slot, async (now, resource) =>
        {
            if (resource.IsArchived)
                return ArchivedResource();
            if (!Schedule.CanBook(slot.StartsAt, now))
                return InvalidSlot();
            if (await GetActiveBooking(slot, now, ct) is not null)
                return Problem(409, "Этот слот уже занят.");

            var booking = NewBooking(slot, owner);
            if (resource.RequiresApproval)
                booking.ApprovalStatus = BookingApproval.Pending;
            db.Bookings.Add(booking);
            return Results.Json(new CreatedBooking(booking.Id, booking.ResourceId, booking.StartUtc, booking.ApprovalStatus),
                statusCode: 201);
        }, ct);

    public Task<IResult> JoinWaitlist(SlotKey slot, Guid owner, CancellationToken ct) =>
        InSlot(slot, async (now, resource) =>
        {
            if (resource.IsArchived)
                return ArchivedResource();
            if (!Schedule.CanBook(slot.StartsAt, now))
                return InvalidSlot();
            var active = await GetActiveBooking(slot, now, ct);
            if (active is null)
                return Problem(409, "Слот свободен. Его можно забронировать.");
            if (active.MaintenanceReason is not null)
                return Problem(409, "На это время запланировано обслуживание.");
            if (active.OwnerId == owner)
                return Problem(409, "Слот уже закреплён за вами.");

            var pending = await Pending(slot).SingleOrDefaultAsync(entry => entry.OwnerId == owner, ct);
            if (pending is not null)
                return Results.Ok(new JoinedWaitlist(pending.Id));

            var entry = new WaitlistEntry { ResourceId = slot.ResourceId, StartUtc = slot.StartsAt, OwnerId = owner };
            db.WaitlistEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            return Results.Json(new JoinedWaitlist(entry.Id), statusCode: 201);
        }, ct);

    public async Task<IResult> LeaveWaitlist(long id, Guid owner, string organizationId, CancellationToken ct)
    {
        var resources = ResourceIds(organizationId);
        var slot = await db.WaitlistEntries.AsNoTracking()
            .Where(entry => entry.Id == id && entry.OwnerId == owner && resources.Contains(entry.ResourceId))
            .Select(entry => (SlotKey?)new SlotKey(entry.ResourceId, entry.StartUtc))
            .SingleOrDefaultAsync(ct);
        if (slot is not { } key)
            return Problem(404, "Запись в листе ожидания не найдена.");

        return await InSlot(key, async (now, _) =>
        {
            var entry = await Pending(key).SingleOrDefaultAsync(item => item.Id == id, ct);
            if (entry is null)
                return Problem(404, "Запись в листе ожидания не найдена.");

            entry.RemovedAt = now;
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> Confirm(Guid id, Guid owner, string organizationId, CancellationToken ct)
    {
        var slot = await FindOwnedSlot(id, owner, organizationId, ct);
        if (slot is not { } key)
            return Problem(404, "Резерв не найден.");

        return await InSlot(key, async (now, resource) =>
        {
            var active = await GetActiveBooking(key, now, ct);
            if (active?.Id != id || key.StartsAt <= now)
                return Problem(409, "Резерв больше недоступен.");

            if (active.HoldExpiresAt is not null)
            {
                active.HoldExpiresAt = null;
                if (resource.RequiresApproval)
                    active.ApprovalStatus = BookingApproval.Pending;
            }
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> Decide(Guid id, string organizationId, string decision, string reason, string actor,
        CancellationToken ct)
    {
        var resources = ResourceIds(organizationId);
        var slot = await db.Bookings.AsNoTracking()
            .Where(booking => booking.Id == id && booking.ApprovalStatus != null && resources.Contains(booking.ResourceId))
            .Select(booking => (SlotKey?)new SlotKey(booking.ResourceId, booking.StartUtc))
            .SingleOrDefaultAsync(ct);
        if (slot is not { } key)
            return Problem(404, "Заявка не найдена.");

        return await InSlot(key, async (now, resource) =>
        {
            var booking = await db.Bookings.SingleAsync(item => item.Id == id, ct);
            if (booking.ApprovalStatus != BookingApproval.Pending || booking.CancelledAt != null || key.StartsAt <= now)
                return Problem(409, "Заявка больше не ожидает решения.");

            booking.ApprovalStatus = decision;
            booking.ApprovalReason = reason;
            Audit.Add(db, resource, actor, $"booking.{decision}", reason, now);
            if (decision == BookingApproval.Rejected)
                await ReleaseAndOfferNext(key, booking, now, ct);
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> Cancel(Guid id, Guid owner, string organizationId, CancellationToken ct)
    {
        var slot = await FindOwnedSlot(id, owner, organizationId, ct);
        if (slot is not { } key)
            return Problem(404, "Активная бронь не найдена.");

        return await InSlot(key, async (now, _) =>
        {
            var active = await GetActiveBooking(key, now, ct);
            if (active?.Id != id || key.StartsAt <= now)
                return Problem(404, "Активная бронь не найдена.");

            await ReleaseAndOfferNext(key, active, now, ct);
            return Results.NoContent();
        }, ct);
    }

    public async Task<IResult> BlockWindow(IReadOnlyList<SlotKey> slots, Guid admin, string actor, string reason,
        CancellationToken ct)
    {
        var resourceId = slots[0].ResourceId;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await LockResource(resourceId, ct);
        if (resource.IsArchived)
            return ArchivedResource();
        foreach (var slot in slots)
            await SlotLock.Acquire(db, slot, ct);

        var now = clock.GetUtcNow();
        if (slots.Any(slot => !Schedule.CanBook(slot.StartsAt, now)))
            return InvalidSlot();
        var requested = slots.Select(slot => slot.StartsAt).ToArray();
        var conflicts = await db.Bookings.AsNoTracking()
            .Where(item => item.ResourceId == resourceId && requested.Contains(item.StartUtc) && item.CancelledAt == null)
            .OrderBy(item => item.StartUtc).Select(item => item.StartUtc).ToArrayAsync(ct);
        if (conflicts.Length > 0)
            return Results.Problem(statusCode: 409, title: "В выбранном интервале есть занятые слоты.",
                extensions: new Dictionary<string, object?> { ["conflicts"] = conflicts });

        var blocks = slots.Select(slot => NewBooking(slot, admin)).ToArray();
        foreach (var block in blocks)
            block.MaintenanceReason = reason;
        db.Bookings.AddRange(blocks);
        Audit.Add(db, resource, actor, "maintenance.created", reason, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(resource.OrganizationId);
        return Results.Json(new CreatedMaintenance(blocks[0].Id, blocks.Length), statusCode: 201);
    }

    public async Task<IResult> Unblock(Guid id, string organizationId, string actor, CancellationToken ct)
    {
        var resources = ResourceIds(organizationId);
        var slot = await db.Bookings.AsNoTracking()
            .Where(booking => booking.Id == id && booking.MaintenanceReason != null
                && resources.Contains(booking.ResourceId))
            .Select(booking => (SlotKey?)new SlotKey(booking.ResourceId, booking.StartUtc))
            .SingleOrDefaultAsync(ct);
        if (slot is not { } key)
            return Problem(404, "Блокировка не найдена.");

        return await InSlot(key, async (now, resource) =>
        {
            var active = await GetActiveBooking(key, now, ct);
            if (active?.Id != id || key.StartsAt <= now)
                return Problem(404, "Активная блокировка не найдена.");

            active.CancelledAt = now;
            Audit.Add(db, resource, actor, "maintenance.removed", active.MaintenanceReason!, now);
            return Results.NoContent();
        }, ct);
    }

    public async Task ExpireHolds(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.Bookings.AsNoTracking()
            .Where(booking => booking.CancelledAt == null && booking.HoldExpiresAt <= now)
            .OrderBy(booking => booking.HoldExpiresAt)
            .Take(100)
            .Select(booking => new SlotKey(booking.ResourceId, booking.StartUtc))
            .ToListAsync(ct);
        foreach (var slot in due)
            await InSlot(slot, (at, _) => GetActiveBooking(slot, at, ct), ct);
    }

    private async Task<T> InSlot<T>(SlotKey slot, Func<DateTimeOffset, Resource, Task<T>> operation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await LockResource(slot.ResourceId, ct);
        await SlotLock.Acquire(db, slot, ct);
        releasedHold = false;
        var result = await operation(clock.GetUtcNow(), resource);
        var changed = await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        if (changed != 0 || releasedHold || result is IStatusCodeHttpResult { StatusCode: 201 })
            await updates.Publish(resource.OrganizationId);
        return result;
    }

    // Keep availability changes ordered with slot writes. Resource locks always come first.
    private async Task<Resource> LockResource(string id, CancellationToken ct) =>
        (await db.Resources.FromSqlInterpolated($"""
            SELECT * FROM "Resources" WHERE "Id" = {id} FOR SHARE
            """).AsNoTracking().ToListAsync(ct)).Single();

    private async Task<Booking?> GetActiveBooking(SlotKey slot, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.Bookings.SingleOrDefaultAsync(booking =>
            booking.ResourceId == slot.ResourceId && booking.StartUtc == slot.StartsAt && booking.CancelledAt == null, ct);
        if (active?.HoldExpiresAt is { } expiry && expiry <= now)
            return await ReleaseAndOfferNext(slot, active, now, ct);
        return active;
    }

    private async Task<Booking?> ReleaseAndOfferNext(SlotKey slot, Booking active, DateTimeOffset now, CancellationToken ct)
    {
        active.CancelledAt = now;
        releasedHold = true;
        // Retire the old row before inserting its replacement under the same unique index.
        await db.SaveChangesAsync(ct);
        if (slot.StartsAt <= now)
            return null;

        var first = await Pending(slot).OrderBy(entry => entry.Id).FirstOrDefaultAsync(ct);
        if (first is null)
            return null;

        first.RemovedAt = now;
        var hold = NewBooking(slot, first.OwnerId);
        var deadline = now.AddSeconds(options.Value.HoldSeconds);
        hold.HoldExpiresAt = deadline < slot.StartsAt ? deadline : slot.StartsAt;
        db.Bookings.Add(hold);
        db.HoldOutbox.Add(new HoldOutboxEvent
        {
            Id = Guid.NewGuid(), BookingId = hold.Id, CreatedAt = now, NextAttemptAt = now
        });
        return hold;
    }

    private IQueryable<WaitlistEntry> Pending(SlotKey slot) =>
        db.WaitlistEntries.Where(entry =>
            entry.ResourceId == slot.ResourceId && entry.StartUtc == slot.StartsAt && entry.RemovedAt == null);

    private IQueryable<string> ResourceIds(string organizationId) =>
        db.Resources.Where(resource => resource.OrganizationId == organizationId).Select(resource => resource.Id);

    private Task<SlotKey?> FindOwnedSlot(Guid id, Guid owner, string organizationId, CancellationToken ct)
    {
        var resources = ResourceIds(organizationId);
        return db.Bookings.AsNoTracking()
            .Where(booking => booking.Id == id && booking.OwnerId == owner && booking.MaintenanceReason == null
                && resources.Contains(booking.ResourceId))
            .Select(booking => (SlotKey?)new SlotKey(booking.ResourceId, booking.StartUtc))
            .SingleOrDefaultAsync(ct);
    }

    private static Booking NewBooking(SlotKey slot, Guid owner) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, ResourceId = slot.ResourceId, StartUtc = slot.StartsAt
    };

    private static IResult InvalidSlot() => Problem(400, "Выберите будущий слот из календаря.");
    private static IResult ArchivedResource() => Problem(409, "Этот ресурс находится в архиве.");
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}

internal static class SlotLock
{
    public static Task Acquire(BookingDb db, SlotKey slot, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(current_schema()::text || ':' || {slot.ResourceId}, {slot.StartsAt.UtcTicks}))",
            ct);
}

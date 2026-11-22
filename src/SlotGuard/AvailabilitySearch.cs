using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class AvailabilitySearch
{
    private const int AlternativeLimit = 6;

    public static async Task<IResult> Find(DateOnly? date, int? hour, string? time, string? timeZone, string? type, int? capacity,
        int? durationMinutes,
        [FromQuery] string[]? equipment, bool? includeAlternatives,
        BookingDb db, OrganizationScope scope, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!Schedule.TryZone(timeZone, out var viewer))
            return BookingEndpoints.InvalidTimeZone();
        var duration = durationMinutes ?? Schedule.DefaultDuration;
        if (!Schedule.Durations.Contains(duration))
            return Results.Problem(statusCode: 400, title: "Выберите 30, 60 или 90 минут.");
        var now = clock.GetUtcNow();
        if (ReadStart(date, hour, time, viewer, now, out var requested) is { } error)
            return error;
        if (type is not null && !ResourceTypes.IsValid(type)
            || capacity is < 1 or > 10000 || !ResourceDetails.ValidEquipment(equipment))
            return Results.Problem(statusCode: 400, title: "Проверьте фильтры типа, вместимости и оснащения.");

        var matches = Filter(scope.Resources(db), type, capacity, ResourceDetails.NormalizeEquipment(equipment));
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await Search(db, matches, requested, duration, DemoAccounts.OwnerId(user), now, includeAlternatives == true, ct);
        await snapshot.CommitAsync(ct);
        return Results.Ok(result);
    }

    internal static IQueryable<Resource> Filter(IQueryable<Resource> source, string? type, int? capacity, string[] equipment)
    {
        var matches = source.AsNoTracking().Where(item => !item.IsArchived);
        if (type is not null)
            matches = matches.Where(item => item.Type == type);
        if (capacity is { } minCapacity)
            matches = matches.Where(item => item.Capacity >= minCapacity);
        foreach (var tag in equipment)
            matches = matches.Where(item => item.Equipment.Contains(tag));
        return matches;
    }

    internal static IResult? ReadStart(DateOnly? date, int? hour, string? time, TimeZoneInfo viewer, DateTimeOffset now,
        out DateTimeOffset[] requested)
    {
        requested = [];
        var today = Schedule.LocalDate(now, viewer);
        var lastDay = Schedule.DisplayLastDay(now, viewer);
        if (date is not { } day || day < today || day > lastDay)
            return InvalidTime();
        TimeOnly requestedTime;
        if (time is null && hour is { } slotHour && Schedule.Hours.Contains(slotHour))
            requestedTime = new TimeOnly(slotHour, 0);
        else if (!Schedule.TryTime(time, out requestedTime))
            return InvalidTime();
        var instants = Schedule.Instants(day, requestedTime, viewer);
        if (instants.Length == 0)
            return Results.Problem(statusCode: 400, title: "Такого местного времени нет из-за перевода часов. Выберите другое время.");
        requested = instants.Where(start => Schedule.InBookableHorizon(start, now)).ToArray();
        if (requested.Length == 0)
            return InvalidTime();
        return null;
    }

    internal static async Task<SearchResult> Search(BookingDb db, IQueryable<Resource> matches, DateTimeOffset[] requested,
        int duration, Guid owner, DateTimeOffset now, bool includeAlternatives, CancellationToken ct)
    {
        var resources = await matches.OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ThenBy(item => item.Id)
            .ToListAsync(ct);
        if (resources.Count == 0)
            return new SearchResult(requested[0], [], [], duration);
        var candidates = resources.Select(item => new
        {
            Resource = new ResourceMatch(item.Id, item.Name, item.Location, item.Type, item.Capacity, item.Equipment,
                item.RequiresApproval),
            Schedule = Schedule.Read(item)
        }).Where(item => item.Schedule.Rules.Allows(duration)).ToArray();

        var limitedIds = candidates.Where(item => item.Schedule.Policies.MaxActiveBookings.HasValue)
            .Select(item => item.Resource.Id).ToArray();
        var used = limitedIds.Length == 0 ? [] : await BookingPolicy.ActiveForLimit(db, now)
            .Where(item => item.OwnerId == owner && limitedIds.Contains(item.ResourceId)).GroupBy(item => item.ResourceId)
            .Select(group => new { Id = group.Key, Count = group.Count() }).ToDictionaryAsync(item => item.Id, item => item.Count, ct);
        var underQuota = candidates.Where(item => item.Schedule.Policies.MaxActiveBookings is not { } max
            || used.GetValueOrDefault(item.Resource.Id) < max).ToArray();
        if (underQuota.Length == 0 && candidates.Length > 0)
            return new SearchResult(requested[0], [], [], duration, "Достигнут лимит броней для ресурсов по этим фильтрам.");
        var external = await ExternalAvailability.Read(db, underQuota.Select(item => item.Resource.Id).ToArray(), now, ct);
        var eligible = underQuota.Where(item => external.GetValueOrDefault(item.Resource.Id)?.Stale != true).ToArray();
        if (eligible.Length == 0 && underQuota.Length > 0)
            return new SearchResult(requested[0], [], [], duration, ExternalAvailability.StaleMessage);

        var horizon = Schedule.MaximumHorizon(now).AddHours(3);
        var occupied = (await db.Bookings.AsNoTracking().Where(item => item.CancelledAt == null
            && item.OccupiedUntilUtc > now && item.OccupiedFromUtc < horizon
            && matches.Any(resource => resource.Id == item.ResourceId)).ToListAsync(ct)).ToLookup(item => item.ResourceId);
        bool IsFree(string id, BookingWindow window) => !occupied[id].Any(item => item.Window.Overlaps(window))
            && external.GetValueOrDefault(id)?.Conflict(window) is null;
        var exact = requested.SelectMany(start => eligible.Where(item => Schedule.CanBook(start, now, item.Schedule, duration)
                && IsFree(item.Resource.Id, item.Schedule.Rules.Window(start, duration)))
            .Select(item => new AvailableSlot(item.Resource, start, start.AddMinutes(duration)))).ToArray();

        AvailableSlot[] alternatives = [];
        if (exact.Length == 0 || includeAlternatives)
        {
            // A pending hold stays occupied until the worker can hand it to the waitlist.
            alternatives = eligible.SelectMany(item => Schedule.FutureStarts(now, item.Schedule, duration)
                    .Where(start => !requested.Contains(start) && IsFree(item.Resource.Id, item.Schedule.Rules.Window(start, duration)))
                    .Select(start => new AvailableSlot(item.Resource, start, start.AddMinutes(duration))))
                .OrderBy(slot => requested.Min(start => (slot.StartsAt - start).Duration())).ThenByDescending(slot => slot.StartsAt)
                .Take(AlternativeLimit).ToArray();
        }
        return new SearchResult(requested[0], exact, alternatives, duration);
    }

    private static IResult InvalidTime() =>
        Results.Problem(statusCode: 400, title: "Выберите будущую дату и время из расписания.");

    internal static async Task<DateTimeOffset[]> OpenStarts(BookingDb db, string resourceId, ResourceSchedule schedule,
        int duration, DateTimeOffset now, CancellationToken ct, Guid? excludedBookingId = null)
    {
        var external = await ExternalAvailability.Read(db, resourceId, now, ct);
        if (external.Stale)
            return [];
        var horizon = schedule.Policies.Horizon(now).AddHours(3);
        var busy = db.Bookings.AsNoTracking().Where(item => item.ResourceId == resourceId && item.CancelledAt == null
            && item.OccupiedUntilUtc > now && item.OccupiedFromUtc < horizon);
        if (excludedBookingId is { } excluded)
            busy = busy.Where(item => item.Id != excluded);
        var windows = (await busy.ToListAsync(ct)).Select(item => item.Window).ToArray();
        return Schedule.FutureStarts(now, schedule, duration)
            .Where(start =>
            {
                var window = schedule.Rules.Window(start, duration);
                return !windows.Any(item => item.Overlaps(window)) && external.Conflict(window) is null;
            }).ToArray();
    }

    internal sealed record ResourceMatch(string Id, string Name, string Location, string Type, int? Capacity, string[] Equipment,
        bool RequiresApproval);
    internal sealed record AvailableSlot(ResourceMatch Resource, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
    internal sealed record SearchResult(DateTimeOffset Requested, AvailableSlot[] Exact, AvailableSlot[] Alternatives, int DurationMinutes,
        string? BookingMessage = null);
}

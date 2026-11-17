using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class AvailabilitySearch
{
    private const int AlternativeLimit = 6;

    public static async Task<IResult> Find(DateOnly? date, int? hour, string? type, int? capacity,
        [FromQuery] string[]? equipment, bool? includeAlternatives,
        BookingDb db, OrganizationScope scope, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = Schedule.LocalDate(now);
        var lastDay = Schedule.Monday(today).AddDays(7 * Schedule.WeekCount - 1);
        if (date is not { } day || day < today || day > lastDay
            || hour is not { } slotHour || !Schedule.Hours.Contains(slotHour))
            return InvalidTime();
        var requested = Schedule.At(day, slotHour);
        if (!Schedule.CanBook(requested, now))
            return InvalidTime();
        if (type is not null && !ResourceTypes.IsValid(type)
            || capacity is < 1 or > 10000 || !ResourceDetails.ValidEquipment(equipment))
            return Results.Problem(statusCode: 400, title: "Проверьте фильтры типа, вместимости и оснащения.");

        var matches = scope.Resources(db).AsNoTracking().Where(item => !item.IsArchived);
        if (type is not null)
            matches = matches.Where(item => item.Type == type);
        if (capacity is { } minCapacity)
            matches = matches.Where(item => item.Capacity >= minCapacity);
        foreach (var tag in ResourceDetails.NormalizeEquipment(equipment))
            matches = matches.Where(item => item.Equipment.Contains(tag));

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resources = await matches.OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ThenBy(item => item.Id)
            .Select(item => new ResourceMatch(item.Id, item.Name, item.Location, item.Type, item.Capacity, item.Equipment,
                item.RequiresApproval))
            .ToArrayAsync(ct);
        if (resources.Length == 0)
            return Results.Ok(new SearchResult(requested, [], []));

        var active = db.Bookings.AsNoTracking().Where(item => item.CancelledAt == null
            && matches.Any(resource => resource.Id == item.ResourceId));
        var occupiedAtRequest = await active.Where(item => item.StartUtc == requested)
            .Select(item => item.ResourceId).ToListAsync(ct);
        var busy = occupiedAtRequest.ToHashSet();
        var exact = resources.Where(item => !busy.Contains(item.Id))
            .Select(item => new AvailableSlot(item, requested)).ToArray();

        AvailableSlot[] alternatives = [];
        if (exact.Length == 0 || includeAlternatives == true)
        {
            // A pending hold stays occupied until the worker can hand it to the waitlist.
            var horizon = Schedule.At(lastDay.AddDays(1), 0);
            var occupied = (await active.Where(item => item.StartUtc > now && item.StartUtc < horizon
                    && item.StartUtc != requested)
                .Select(item => new SlotKey(item.ResourceId, item.StartUtc)).ToListAsync(ct)).ToHashSet();
            var nearby = Enumerable.Range(0, lastDay.DayNumber - today.DayNumber + 1)
                .SelectMany(offset => Schedule.Hours.Select(value => Schedule.At(today.AddDays(offset), value)))
                .Where(startsAt => startsAt > now && startsAt != requested)
                .OrderBy(startsAt => (startsAt - requested).Duration()).ThenByDescending(startsAt => startsAt);
            alternatives = nearby.SelectMany(startsAt => resources
                    .Where(item => !occupied.Contains(new SlotKey(item.Id, startsAt)))
                    .Select(item => new AvailableSlot(item, startsAt)))
                .Take(AlternativeLimit).ToArray();
        }
        await snapshot.CommitAsync(ct);
        return Results.Ok(new SearchResult(requested, exact, alternatives));
    }

    private static IResult InvalidTime() =>
        Results.Problem(statusCode: 400, title: "Выберите будущую дату и время из расписания.");

    internal sealed record ResourceMatch(string Id, string Name, string Location, string Type, int? Capacity, string[] Equipment,
        bool RequiresApproval);
    internal sealed record AvailableSlot(ResourceMatch Resource, DateTimeOffset StartsAt);
    internal sealed record SearchResult(DateTimeOffset Requested, AvailableSlot[] Exact, AvailableSlot[] Alternatives);
}

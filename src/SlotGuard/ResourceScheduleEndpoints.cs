using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class ResourceScheduleEndpoints
{
    public static void MapResourceSchedules(this RouteGroupBuilder workspace)
    {
        workspace.MapGet("/resources/{id}/schedule", Get);
        workspace.MapPut("/resources/{id}/schedule", Save);
    }

    private static async Task<IResult> Get(string id, BookingDb db, OrganizationScope scope, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var resource = await scope.Resources(db).AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (resource is null)
            return Results.NotFound();
        var definition = Canonical(Schedule.Read(resource));
        var template = await ResourceTemplateEndpoints.LockInfo(db, resource, ct);
        return Results.Ok(new { definition.Week, definition.Exceptions, definition.TimeZone, definition.Booking, definition.Policy, Template = template });
    }

    private static async Task<IResult> Save(string id, ResourceSchedule input, ClaimsPrincipal user, BookingDb db,
        OrganizationScope scope, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (Validate(input, out var normalized) is { } invalid)
            return invalid;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Results.NotFound();
        var previous = Canonical(Schedule.Read(resource));
        var definition = normalized! with { TimeZone = normalized!.TimeZone ?? previous.ZoneId,
            Booking = normalized.Booking ?? previous.Rules, Policy = normalized.Policy ?? previous.Policies };

        var template = await ResourceTemplateEndpoints.LockInfo(db, resource, ct);
        if (template?.LockSchedule == true && !SameWeek(previous, definition))
            return ResourceTemplateEndpoints.Locked(template.Name, "графиком");
        if (template?.LockRules == true && (definition.Rules != previous.Rules || definition.Policies != previous.Policies))
            return ResourceTemplateEndpoints.Locked(template.Name, "правилами бронирования");

        var json = Schedule.Write(definition);
        if (json == Schedule.Write(previous))
            return Results.Ok(definition);

        var now = clock.GetUtcNow();
        var conflicts = await Conflicts(db, id, definition, now, ct);
        if (conflicts.Length > 0)
            return Results.Problem(statusCode: 409, title: "График закрывает слоты с активными записями.",
                extensions: new Dictionary<string, object?> { ["conflicts"] = conflicts });

        await scope.Resources(db).Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.ScheduleDefinition, json), ct);
        var policyChanged = previous.Policies != definition.Policies;
        var details = policyChanged ? "Обновлены правила бронирования" : previous.ZoneId == definition.ZoneId ? "Изменён график работы"
            : $"Часовой пояс {previous.ZoneId} → {definition.ZoneId}";
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), policyChanged ? "resource.booking-policy" : "resource.schedule", details, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(resource.OrganizationId);
        return Results.Ok(definition);
    }

    internal static ResourceSchedule Canonical(ResourceSchedule definition) =>
        definition with { TimeZone = definition.ZoneId, Booking = definition.Rules, Policy = definition.Policies };

    internal static bool SameWeek(ResourceSchedule left, ResourceSchedule right) =>
        left.ZoneId == right.ZoneId && left.Week.Zip(right.Week).All(pair =>
            pair.First.Hours.SequenceEqual(pair.Second.Hours) && pair.First.Breaks.SequenceEqual(pair.Second.Breaks));

    internal static IResult? Validate(ResourceSchedule input, out ResourceSchedule? definition)
    {
        definition = null;
        if (!Schedule.TryZone(input.TimeZone, out _))
            return Results.Problem(statusCode: 400, title: "Выберите существующий часовой пояс ресурса.");
        if (input.Booking is { } rules && !rules.IsValid())
            return Results.Problem(statusCode: 400, title: "Укажите буферы от 0 до 60 минут с шагом 5 минут для гибкого бронирования.");
        if (input.Policy is { } policy && !policy.IsValid())
            return Results.Problem(statusCode: 400, title: "Проверьте сроки и лимит. Минимальный срок записи должен помещаться в окно бронирования.");
        if (input.Normalize() is not { } normalized)
            return Results.Problem(statusCode: 400,
                title: "Проверьте рабочие часы, перерывы и даты. Интервалы одного вида должны идти без пересечений, а перерывы — внутри рабочих часов.");
        definition = normalized;
        return null;
    }

    internal static async Task<DateTimeOffset[]> Conflicts(BookingDb db, string id, ResourceSchedule definition,
        DateTimeOffset now, CancellationToken ct)
    {
        var occupied = db.Bookings.AsNoTracking().Where(item => item.ResourceId == id && item.CancelledAt == null
            && item.OccupiedUntilUtc > now);
        var waiting = db.WaitlistEntries.AsNoTracking().Where(item => item.ResourceId == id && item.RemovedAt == null
            && item.StartUtc > now);
        var bookings = await occupied.ToListAsync(ct);
        var entries = await waiting.ToListAsync(ct);
        return bookings.Where(item => !Schedule.FitsHours(item.Window, definition)
                || item.MaintenanceReason is null && !definition.Rules.Allows((int)(item.EndUtc - item.StartUtc).TotalMinutes))
            .Select(item => item.StartUtc)
            .Union(entries.Where(item => !Schedule.Contains(item.StartUtc, definition, item.DurationMinutes)).Select(item => item.StartUtc))
            .Order().ToArray();
    }
}

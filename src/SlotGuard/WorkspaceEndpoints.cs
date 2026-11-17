using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        var workspace = api.MapGroup("/workspace");
        workspace.MapGet("/overview", Overview);
        workspace.MapGet("/me", MySchedule);
        workspace.MapGet("/resources", ListResources);
        workspace.MapPost("/resources", CreateResource);
        workspace.MapPut("/resources/{id}", EditResource);
        workspace.MapGet("/activity", ListActivity);
    }

    private static async Task<IResult> Overview(DateOnly? week, BookingDb db, OrganizationScope scope,
        TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var firstWeek = Schedule.Monday(Schedule.LocalDate(now));
        var lastWeek = firstWeek.AddDays(7 * (Schedule.WeekCount - 1));
        var monday = week ?? firstWeek;
        if (monday < firstWeek || monday > lastWeek || monday.DayOfWeek != DayOfWeek.Monday)
            return Results.Problem(statusCode: 400, title: "Выберите понедельник в пределах календаря.");

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resources = await scope.Resources(db).AsNoTracking().Where(resource => !resource.IsArchived)
            .OrderBy(resource => resource.SortOrder).ThenBy(resource => resource.Name).ThenBy(resource => resource.Id)
            .ToListAsync(ct);
        var ids = resources.Select(resource => resource.Id).ToArray();
        var start = Schedule.At(monday, 0);
        var end = Schedule.At(monday.AddDays(7), 0);
        var rows = await db.Bookings.AsNoTracking().Where(item => ids.Contains(item.ResourceId)
            && item.StartUtc >= start && item.StartUtc < end && item.StartUtc > now && item.CancelledAt == null)
            .Select(item => new Occupancy(item.ResourceId, item.StartUtc,
                item.MaintenanceReason != null ? "maintenance" : item.HoldExpiresAt != null ? "held"
                : item.ApprovalStatus == BookingApproval.Pending ? "pending" : "booked"))
            .ToListAsync(ct);
        var waiting = await db.WaitlistEntries.AsNoTracking().CountAsync(item => ids.Contains(item.ResourceId)
            && item.StartUtc >= start && item.StartUtc < end && item.StartUtc > now && item.RemovedAt == null, ct);
        await snapshot.CommitAsync(ct);

        var byDay = rows.ToLookup(item => Schedule.LocalDate(item.StartsAt));
        var byResource = rows.ToLookup(item => item.ResourceId);
        var days = Enumerable.Range(0, 7).Select(monday.AddDays).Select(day =>
        {
            var capacity = Schedule.Hours.Count(hour => Schedule.At(day, hour) > now);
            return new { Date = day, Counts = Count(byDay[day], ids.Length * capacity), Capacity = capacity };
        }).ToArray();
        var slotsPerResource = days.Sum(day => day.Capacity);
        return Results.Ok(new
        {
            Week = monday, FirstWeek = firstWeek, LastWeek = lastWeek, Days = days,
            Totals = Count(rows, ids.Length * slotsPerResource), Waiting = waiting,
            Resources = resources.Select(resource => new
            {
                resource.Id, resource.Name, resource.Location,
                Counts = Count(byResource[resource.Id], slotsPerResource)
            })
        });
    }

    private static Totals Count(IEnumerable<Occupancy> items, int capacity)
    {
        var groups = items.ToLookup(item => item.Status);
        var booked = groups["booked"].Count();
        var held = groups["held"].Count();
        var maintenance = groups["maintenance"].Count();
        var pending = groups["pending"].Count();
        return new Totals(capacity - booked - held - maintenance - pending, booked, held, maintenance, pending);
    }

    private static async Task<IResult> MySchedule(BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var owner = DemoAccounts.OwnerId(user);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var myBookings =
            from booking in db.Bookings.AsNoTracking()
            join resource in scope.Resources(db) on booking.ResourceId equals resource.Id
            where booking.OwnerId == owner && booking.MaintenanceReason == null
            select new
            {
                booking.Id, booking.ResourceId, ResourceName = resource.Name, resource.Location,
                StartsAt = booking.StartUtc, booking.HoldExpiresAt, booking.CancelledAt,
                booking.ApprovalStatus, booking.ApprovalReason, resource.RequiresApproval
            };
        var upcoming = await myBookings.Where(item => item.StartsAt > now && item.CancelledAt == null
                && (item.HoldExpiresAt == null || item.HoldExpiresAt > now))
            .OrderBy(item => item.StartsAt).ThenBy(item => item.Id).Take(60).ToListAsync(ct);
        var history = await myBookings.Where(item => item.StartsAt <= now || item.CancelledAt != null
                || item.HoldExpiresAt <= now)
            .OrderByDescending(item => item.StartsAt).ThenBy(item => item.Id).Take(30).ToListAsync(ct);
        var pending = db.WaitlistEntries.AsNoTracking().Where(item => item.RemovedAt == null && item.StartUtc > now);
        var waiting = await (
                from entry in pending
                join resource in scope.Resources(db) on entry.ResourceId equals resource.Id
                where entry.OwnerId == owner
                orderby entry.StartUtc, entry.Id
                select new
                {
                    entry.Id, entry.ResourceId, ResourceName = resource.Name, resource.Location, StartsAt = entry.StartUtc,
                    Position = pending.Count(other => other.ResourceId == entry.ResourceId && other.StartUtc == entry.StartUtc
                        && other.Id <= entry.Id)
                }).Take(60).ToListAsync(ct);
        await snapshot.CommitAsync(ct);
        return Results.Ok(new { upcoming, history, waiting });
    }

    private static async Task<IResult> ListResources(BookingDb db, OrganizationScope scope, CancellationToken ct) =>
        scope.IsAdmin
            ? Results.Ok(await scope.Resources(db).AsNoTracking().OrderBy(item => item.IsArchived)
                .ThenBy(item => item.SortOrder).ThenBy(item => item.Name).ThenBy(item => item.Id).ToListAsync(ct))
            : Results.StatusCode(403);

    private static async Task<IResult> CreateResource(ResourceInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!ValidResource(input))
            return InvalidResource();
        var resource = new Resource(Guid.NewGuid().ToString("N"), input.Name!.Trim(), input.Location!.Trim(),
            100, scope.Current.Id) { RequiresApproval = input.RequiresApproval ?? false };
        if (input.Details is not null)
            resource = input.Details.ApplyTo(resource);
        db.Resources.Add(resource);
        Audit.Add(db, resource, Actor(user), "resource.created", resource.Location, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Json(resource, statusCode: 201);
    }

    private static async Task<IResult> EditResource(string id, ResourceInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!ValidResource(input))
            return InvalidResource();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resources = await db.Resources.FromSqlInterpolated($"""
            SELECT * FROM "Resources" WHERE "Id" = {id} AND "OrganizationId" = {scope.Current.Id} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        var resource = resources.SingleOrDefault();
        if (resource is null)
            return Results.NotFound();

        var now = clock.GetUtcNow();
        if (input.IsArchived && !resource.IsArchived && await db.Bookings.AnyAsync(
                item => item.ResourceId == id && item.CancelledAt == null && item.StartUtc > now, ct))
            return Results.Problem(statusCode: 409, title: "Сначала завершите или отмените будущие записи по этому ресурсу.");

        var updated = resource with
        {
            Name = input.Name!.Trim(), Location = input.Location!.Trim(), IsArchived = input.IsArchived,
            RequiresApproval = input.RequiresApproval ?? resource.RequiresApproval
        };
        if (input.Details is not null)
            updated = input.Details.ApplyTo(updated);
        if (updated == resource)
            return Results.Ok(resource);

        await scope.Resources(db).Where(item => item.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(item => item.Name, updated.Name)
            .SetProperty(item => item.Location, updated.Location)
            .SetProperty(item => item.Type, updated.Type)
            .SetProperty(item => item.Capacity, updated.Capacity)
            .SetProperty(item => item.Equipment, updated.Equipment)
            .SetProperty(item => item.RequiresApproval, updated.RequiresApproval)
            .SetProperty(item => item.IsArchived, updated.IsArchived), ct);
        var action = resource.IsArchived == updated.IsArchived ? "resource.updated"
            : updated.IsArchived ? "resource.archived" : "resource.restored";
        Audit.Add(db, updated, Actor(user), action, updated.Location, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (updated.RequiresApproval != resource.RequiresApproval)
            await updates.Publish(scope.Current.Id);
        return Results.Ok(updated);
    }

    private static async Task<IResult> ListActivity(BookingDb db, OrganizationScope scope, CancellationToken ct) =>
        scope.IsAdmin
            ? Results.Ok(await db.WorkspaceActivities.AsNoTracking().Where(item => item.OrganizationId == scope.Current.Id)
                .OrderByDescending(item => item.Id).Take(50)
                .Select(item => new { item.ResourceName, item.Actor, item.Action, item.Details, item.OccurredAt })
                .ToListAsync(ct))
            : Results.StatusCode(403);

    private static bool ValidResource(ResourceInput input) =>
        input.Name?.Trim().Length is > 0 and <= 100 && input.Location?.Trim().Length is > 0 and <= 140
        && (input.Details?.IsValid() ?? true);

    private static IResult InvalidResource() =>
        Results.Problem(statusCode: 400,
            title: "Проверьте название, расположение, тип, вместимость и список оснащения ресурса.");

    internal static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.GivenName)
        ?? user.Identity?.Name ?? "Администратор";
    private sealed record ResourceInput(string? Name, string? Location, bool IsArchived = false, ResourceDetails? Details = null,
        bool? RequiresApproval = null);
    private sealed record Occupancy(string ResourceId, DateTimeOffset StartsAt, string Status);
    private sealed record Totals(int Free, int Booked, int Held, int Maintenance, int Pending);
}

internal static class Audit
{
    public static void Add(BookingDb db, Resource resource, string actor, string action, string details, DateTimeOffset now) =>
        db.WorkspaceActivities.Add(new WorkspaceActivity
        {
            OrganizationId = resource.OrganizationId, ResourceId = resource.Id, ResourceName = resource.Name,
            Actor = actor, Action = action, Details = details, OccurredAt = now
        });
}

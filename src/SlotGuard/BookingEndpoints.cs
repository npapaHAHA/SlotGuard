using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using static SlotGuard.DemoAccounts;

namespace SlotGuard;

internal static class BookingEndpoints
{
    public static void MapBookingEndpoints(this WebApplication app)
    {
        var root = app.MapGroup("/api").RequireAuthorization();
        root.MapPost("/session", Session).AllowAnonymous();
        root.MapPost("/session/demo", SignInDemo).AllowAnonymous();
        root.MapPost("/session/guest", StartGuest);
        root.MapGet("/organizations", GetOrganizations);

        var api = root.MapGroup("").AddEndpointFilter<OrganizationFilter>();
        api.MapWorkspaceEndpoints();
        api.MapTeamEndpoints();
        api.MapGet("/resources", GetResources);
        api.MapGet("/calendar", GetCalendar);
        api.MapGet("/availability", AvailabilitySearch.Find);
        api.MapGet("/notifications", GetNotifications);
        api.MapPost("/bookings", BookSlot)
            .Produces<CreatedBooking>(201)
            .Produces(401)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        api.MapDelete("/bookings/{id:guid}", CancelBooking)
            .Produces(204)
            .Produces(401)
            .ProducesProblem(404);
        api.MapPost("/bookings/{id:guid}/confirm", ConfirmHold)
            .Produces(204)
            .Produces(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        api.MapPost("/waitlist", JoinWaitlist)
            .Produces<JoinedWaitlist>(201)
            .Produces<JoinedWaitlist>(200)
            .Produces(401)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        api.MapDelete("/waitlist/{id:long}", LeaveWaitlist)
            .Produces(204)
            .Produces(401)
            .ProducesProblem(404);
        api.MapPost("/maintenance", BlockSlot)
            .Produces<CreatedMaintenance>(201)
            .ProducesProblem(400)
            .Produces(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
        api.MapDelete("/maintenance/{id:guid}", UnblockSlot)
            .Produces(204)
            .Produces(403)
            .ProducesProblem(404);
        api.MapPost("/maintenance/window", BlockWindow);
    }

    private static async Task<IResult> GetOrganizations(BookingDb db, ClaimsPrincipal user, CancellationToken ct) =>
        Results.Ok(await OrganizationScope.ForUser(db, user, ct));

    private static async Task<IResult> GetResources(BookingDb db, OrganizationScope scope, CancellationToken ct) =>
        Results.Ok(await scope.Resources(db).AsNoTracking()
            .Where(resource => !resource.IsArchived)
            .OrderBy(resource => resource.SortOrder).ThenBy(resource => resource.Name).ThenBy(resource => resource.Id)
            .Select(resource => new { resource.Id, resource.Name, resource.Location, resource.Type, resource.Capacity, resource.Equipment })
            .ToListAsync(ct));

    private static async Task<IResult> GetNotifications(BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var owner = OwnerId(user);
        var notifications = await (
            from notification in db.HoldNotifications.AsNoTracking()
            join booking in db.Bookings on notification.BookingId equals booking.Id
            join resource in scope.Resources(db) on booking.ResourceId equals resource.Id
            where booking.OwnerId == owner && booking.CancelledAt == null && booking.HoldExpiresAt > now
            orderby notification.DeliveredAt descending, notification.EventId
            select new
            {
                notification.EventId,
                notification.BookingId,
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                StartsAt = booking.StartUtc,
                booking.HoldExpiresAt
            }
        ).Take(20).ToListAsync(ct);

        return Results.Ok(notifications.Select(notification => new
        {
            notification.EventId,
            notification.BookingId,
            notification.ResourceId,
            notification.ResourceName,
            notification.StartsAt,
            notification.HoldExpiresAt,
            Week = Schedule.Monday(Schedule.LocalDate(notification.StartsAt))
        }));
    }

    private static async Task<IResult> GetCalendar(
        string resourceId, DateOnly? week, ClaimsPrincipal user, TimeProvider clock,
        BookingDb db, OrganizationScope scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = Schedule.LocalDate(now);
        var firstWeek = Schedule.Monday(today);
        var lastWeek = firstWeek.AddDays(7 * (Schedule.WeekCount - 1));
        var monday = week ?? firstWeek;
        if (monday < firstWeek || monday > lastWeek || monday.DayOfWeek != DayOfWeek.Monday)
            return Results.Problem(statusCode: 400, title: "Выберите понедельник в пределах календаря.");
        if (!await scope.Resources(db).AnyAsync(resource => resource.Id == resourceId && !resource.IsArchived, ct))
            return Results.NotFound();

        var start = Schedule.At(monday, 0);
        var end = Schedule.At(monday.AddDays(7), 0);
        var ownerId = OwnerId(user);
        // Read the booking and queue position from the same snapshot during a handoff.
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var active = await db.Bookings.AsNoTracking()
            .Where(booking => booking.ResourceId == resourceId && booking.CancelledAt == null
                && booking.StartUtc >= start && booking.StartUtc < end)
            .ToDictionaryAsync(booking => booking.StartUtc, ct);
        var pending = db.WaitlistEntries.AsNoTracking()
            .Where(entry => entry.ResourceId == resourceId && entry.RemovedAt == null
                && entry.StartUtc > now && entry.StartUtc >= start && entry.StartUtc < end);
        var positions = await pending.Where(entry => entry.OwnerId == ownerId)
            .Select(entry => new
            {
                entry.StartUtc,
                entry.Id,
                Position = pending.Count(other => other.StartUtc == entry.StartUtc && other.Id <= entry.Id)
            })
            .ToDictionaryAsync(entry => entry.StartUtc, ct);
        await snapshot.CommitAsync(ct);

        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = monday.AddDays(offset);
            var slots = Schedule.Hours.Select(hour =>
            {
                var startsAt = Schedule.At(date, hour);
                active.TryGetValue(startsAt, out var booking);
                var block = startsAt > now && booking?.MaintenanceReason is not null ? booking : null;
                var ownBooking = startsAt > now && block is null && booking?.OwnerId == ownerId ? booking : null;
                positions.TryGetValue(startsAt, out var entry);
                var status = startsAt <= now ? "past"
                    : booking is null ? "available"
                    : block is not null ? "maintenance"
                    : ownBooking is null ? "booked"
                    : ownBooking.HoldExpiresAt is null ? "mine" : "held";
                return new CalendarSlot(startsAt, status, ownBooking?.Id, ownBooking?.HoldExpiresAt,
                    entry?.Id, entry?.Position, block?.MaintenanceReason, scope.IsAdmin ? block?.Id : null);
            });
            return new { date, slots };
        });

        return Results.Ok(new { today, week = monday, firstWeek, lastWeek, days });
    }

    private static Task<IResult> BookSlot(
        SlotRequest request, ClaimsPrincipal user, TimeProvider clock, BookingDb db,
        OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        ForRequestedSlot(request, clock, db, scope, slot => bookings.Book(slot, OwnerId(user), ct), ct);

    private static Task<IResult> JoinWaitlist(
        SlotRequest request, ClaimsPrincipal user, TimeProvider clock, BookingDb db,
        OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        ForRequestedSlot(request, clock, db, scope, slot => bookings.JoinWaitlist(slot, OwnerId(user), ct), ct);

    private static async Task<IResult> ForRequestedSlot(
        SlotRequest request, TimeProvider clock, BookingDb db, OrganizationScope scope,
        Func<SlotKey, Task<IResult>> action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 32
            || !Schedule.CanBook(request.StartsAt, clock.GetUtcNow()))
        {
            return Results.Problem(statusCode: 400, title: "Выберите будущий слот из календаря.");
        }

        if (!await scope.Resources(db).AnyAsync(resource => resource.Id == request.ResourceId, ct))
            return Results.Problem(statusCode: 404, title: "Ресурс не найден.");

        return await action(new SlotKey(request.ResourceId, request.StartsAt.ToUniversalTime()));
    }

    private static Task<IResult> CancelBooking(
        Guid id, ClaimsPrincipal user, OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        bookings.Cancel(id, OwnerId(user), scope.Current.Id, ct);

    private static Task<IResult> ConfirmHold(
        Guid id, ClaimsPrincipal user, OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        bookings.Confirm(id, OwnerId(user), scope.Current.Id, ct);

    private static Task<IResult> LeaveWaitlist(
        long id, ClaimsPrincipal user, OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        bookings.LeaveWaitlist(id, OwnerId(user), scope.Current.Id, ct);

    private static async Task<IResult> BlockSlot(
        MaintenanceRequest request, ClaimsPrincipal user, TimeProvider clock, BookingDb db,
        OrganizationScope scope, SlotBookings bookings, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 160)
            return Results.Problem(statusCode: 400, title: "Укажите причину обслуживания длиной до 160 символов.");

        return await ForRequestedSlot(new SlotRequest(request.ResourceId, request.StartsAt), clock, db, scope,
            slot => bookings.BlockWindow([slot], OwnerId(user), WorkspaceEndpoints.Actor(user), reason, ct), ct);
    }

    private static async Task<IResult> BlockWindow(MaintenanceWindowRequest request, ClaimsPrincipal user,
        TimeProvider clock, BookingDb db, OrganizationScope scope, SlotBookings bookings, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 160 || request.FromHour > request.ThroughHour
            || !Schedule.Hours.Contains(request.FromHour) || !Schedule.Hours.Contains(request.ThroughHour))
            return Results.Problem(statusCode: 400, title: "Выберите часы работы ресурса и укажите причину до 160 символов.");

        var first = Schedule.At(request.Date, request.FromHour);
        var slots = Schedule.Hours.Where(hour => hour >= request.FromHour && hour <= request.ThroughHour)
            .Select(hour => new SlotKey(request.ResourceId, Schedule.At(request.Date, hour))).ToArray();
        return await ForRequestedSlot(new SlotRequest(request.ResourceId, first), clock, db, scope,
            _ => bookings.BlockWindow(slots, OwnerId(user), WorkspaceEndpoints.Actor(user), reason, ct), ct);
    }

    private static Task<IResult> UnblockSlot(Guid id, OrganizationScope scope, ClaimsPrincipal user,
        SlotBookings bookings, CancellationToken ct) =>
        scope.IsAdmin ? bookings.Unblock(id, scope.Current.Id, WorkspaceEndpoints.Actor(user), ct)
            : Task.FromResult(Results.StatusCode(403));

    private sealed record SlotRequest(string ResourceId, DateTimeOffset StartsAt);
    private sealed record MaintenanceRequest(string ResourceId, DateTimeOffset StartsAt, string? Reason);
    private sealed record MaintenanceWindowRequest(string ResourceId, DateOnly Date, int FromHour, int ThroughHour, string? Reason);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId,
        DateTimeOffset? HoldExpiresAt, long? WaitlistId, int? WaitlistPosition, string? MaintenanceReason, Guid? MaintenanceId);
}

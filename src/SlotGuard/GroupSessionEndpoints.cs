using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record GroupSessionInput(string? ResourceId, string? Title, DateTimeOffset StartsAt, int DurationMinutes,
    int Seats, bool RequiresApproval = false);
internal sealed record SeatInput(BookingFormSubmission? BookingForm);
internal sealed record SeatDecision(Guid[]? SeatIds, string? Action, string? Reason);
internal sealed record SeatView(Guid Id, string Status, string? DecisionReason, string? Attendance, DateTimeOffset? AttendanceAt);
internal sealed record SessionCard(Guid Id, string ResourceId, string ResourceName, string Location, string Title,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset? CancelledAt, int Capacity, int RemainingSeats,
    bool RequiresApproval, SeatView? Mine);
internal sealed record RosterEntry(SeatView Seat, string Name, BookingAnswer[] Answers);
internal sealed record SessionView(SessionCard Session, BookingForm Form, bool CanJoin, bool CanCancelSeat,
    string? BookingMessage, RosterEntry[]? Participants);

internal static class GroupSessionEndpoints
{
    public static void MapGroupSessions(this RouteGroupBuilder api)
    {
        var sessions = api.MapGroup("/group-sessions");
        sessions.MapGet("", List);
        sessions.MapGet("/{id:guid}", Details);
        sessions.MapGet("/resources/{id}/starts", Starts);
        sessions.MapPost("", Create);
        sessions.MapDelete("/{id:guid}", Cancel);
        sessions.MapPost("/{id:guid}/seat", Join);
        sessions.MapDelete("/{id:guid}/seat", Leave);
        sessions.MapPost("/{id:guid}/participants", Decide);
    }

    internal sealed record ScheduledSession(Booking Booking, Resource Resource);
    internal static IQueryable<Booking> ScopedSessions(BookingDb db, IQueryable<Resource> resources) =>
        db.Bookings.Where(session => session.SeatCapacity != null && resources.Any(resource => resource.Id == session.ResourceId));

    private static async Task<IResult> List(bool? mine, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var owner = DemoAccounts.OwnerId(user);
        var now = clock.GetUtcNow();
        var matches = ScopedSessions(db, scope.Resources(db)).AsNoTracking();
        if (mine == true)
            matches = matches.Where(item => db.SeatReservations.Any(seat => seat.SessionId == item.Id && seat.OwnerId == owner));
        else
        {
            var since = scope.IsAdmin ? now.AddDays(-30) : now;
            matches = matches.Where(item => item.CancelledAt == null && item.EndUtc > since);
        }

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var sessions = await (from session in matches
            join resource in scope.Resources(db).AsNoTracking() on session.ResourceId equals resource.Id
            orderby (session.EndUtc > now && session.CancelledAt == null) descending,
                (session.EndUtc > now ? session.StartUtc : DateTimeOffset.MaxValue), session.StartUtc descending, session.Id
            select new ScheduledSession(session, resource)).Take(80).ToArrayAsync(ct);
        var ids = sessions.Select(item => item.Booking.Id).ToArray();
        var used = await db.SeatReservations.AsNoTracking().Active().Where(item => ids.Contains(item.SessionId))
            .GroupBy(item => item.SessionId).ToDictionaryAsync(group => group.Key, group => group.Count(), ct);
        var own = (await db.SeatReservations.AsNoTracking().Where(item => item.OwnerId == owner && ids.Contains(item.SessionId))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToArrayAsync(ct))
            .ToLookup(item => item.SessionId);
        await snapshot.CommitAsync(ct);

        return Results.Ok(sessions.Select(item => Card(item, used.GetValueOrDefault(item.Booking.Id),
            own[item.Booking.Id].FirstOrDefault())));
    }

    private static async Task<IResult> Details(Guid id, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resources = scope.Resources(db).AsNoTracking();
        var session = await (from booking in ScopedSessions(db, resources).AsNoTracking()
            join resource in resources on booking.ResourceId equals resource.Id
            where booking.Id == id
            select new ScheduledSession(booking, resource)).SingleOrDefaultAsync(ct);
        if (session is null)
            return Results.NotFound();

        var now = clock.GetUtcNow();
        var owner = DemoAccounts.OwnerId(user);
        var seats = db.SeatReservations.AsNoTracking().Where(item => item.SessionId == id);
        var own = await seats.Where(item => item.OwnerId == owner).OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id).FirstOrDefaultAsync(ct);
        var model = Card(session, await seats.Active().CountAsync(ct), own);
        var activeSeat = own?.Status is SeatStatus.Pending or SeatStatus.Confirmed;
        var schedule = Schedule.Read(session.Resource);
        var open = model.CancelledAt is null && model.StartsAt > now;
        var canCancel = open && activeSeat && schedule.Policies.Changes(model.StartsAt, now).CanCancel;
        string? message = null;
        if (open && !activeSeat && model.RemainingSeats > 0)
        {
            var external = await ExternalAvailability.Read(db, session.Resource.Id, now, ct);
            message = schedule.Policies.TimeRestriction(model.StartsAt, now) ?? external.Conflict(session.Booking.Window);
            if (message is null && await schedule.Policies.AtLimit(db, session.Resource.Id, owner, now, ct))
                message = schedule.Policies.LimitMessage;
        }
        RosterEntry[]? participants = null;
        if (scope.IsAdmin)
        {
            var roster = await (from seat in seats
                                join account in db.Users.AsNoTracking() on seat.OwnerId equals account.Id into owners
                                from account in owners.DefaultIfEmpty()
                                orderby seat.CreatedAt, seat.Id
                                select new { Seat = seat, Name = account == null ? null : account.DisplayName }).ToArrayAsync(ct);
            participants = roster.Select(item => new RosterEntry(View(item.Seat),
                item.Name ?? item.Seat.GuestName ?? DemoAccounts.LabelFor(item.Seat.OwnerId), BookingForm.ReadAnswers(item.Seat.AnswersJson))).ToArray();
        }
        await snapshot.CommitAsync(ct);
        return Results.Ok(new SessionView(model, BookingForm.Read(session.Resource),
            open && !activeSeat && model.RemainingSeats > 0 && message is null, canCancel, message, participants));
    }

    private static async Task<IResult> Starts(string id, int? durationMinutes, BookingDb db, OrganizationScope scope,
        TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var resource = await scope.Resources(db).AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && !item.IsArchived, ct);
        if (resource is null)
            return Results.NotFound();
        if (!CanHost(resource))
            return Results.Problem(statusCode: 422, title: "Выберите помещение или другой ресурс для группового занятия.");
        var schedule = Schedule.Read(resource);
        var duration = durationMinutes ?? Schedule.DefaultDuration;
        if (!schedule.Rules.Allows(duration))
            return Results.Problem(statusCode: 422, title: "Для этого ресурса недоступна такая длительность.");
        var starts = await AvailabilitySearch.OpenStarts(db, id, schedule, duration, clock.GetUtcNow(), ct);
        return Results.Ok(new { Starts = starts, Capacity = CapacityFor(resource), resource.RequiresApproval });
    }

    private static Task<IResult> Create(GroupSessionInput input, BookingDb db, SlotBookings bookings, OrganizationScope scope,
        ClaimsPrincipal user, CancellationToken ct) => scope.IsAdmin
        ? bookings.CreateGroupSession(scope.Resources(db), input, DemoAccounts.OwnerId(user), WorkspaceEndpoints.Actor(user), ct)
        : Task.FromResult(Results.StatusCode(403));

    private static Task<IResult> Cancel(Guid id, BookingDb db, SlotBookings bookings, OrganizationScope scope,
        ClaimsPrincipal user, CancellationToken ct) => scope.IsAdmin
        ? bookings.CancelGroupSession(scope.Resources(db), id, WorkspaceEndpoints.Actor(user), ct)
        : Task.FromResult(Results.StatusCode(403));

    private static Task<IResult> Join(Guid id, SeatInput? input, BookingDb db, SlotBookings bookings, OrganizationScope scope,
        ClaimsPrincipal user, CancellationToken ct) =>
        bookings.BookSeat(scope.Resources(db), id, DemoAccounts.OwnerId(user), input?.BookingForm, ct);

    private static Task<IResult> Leave(Guid id, BookingDb db, SlotBookings bookings, OrganizationScope scope,
        ClaimsPrincipal user, CancellationToken ct) =>
        bookings.CancelSeat(scope.Resources(db), id, DemoAccounts.OwnerId(user), ct);

    private static Task<IResult> Decide(Guid id, SeatDecision input, BookingDb db, SlotBookings bookings, OrganizationScope scope,
        ClaimsPrincipal user, CancellationToken ct) => scope.IsAdmin
        ? bookings.DecideSeats(scope.Resources(db), id, input, WorkspaceEndpoints.Actor(user), ct)
        : Task.FromResult(Results.StatusCode(403));

    internal const int MaxSeats = 100;
    internal static int CapacityFor(Resource resource) => Math.Min(resource.Capacity ?? MaxSeats, MaxSeats);
    internal static bool CanHost(Resource resource) => resource.Type != ResourceTypes.Equipment && CapacityFor(resource) >= 2;

    internal static SeatView View(SeatReservation item) =>
        new(item.Id, item.Status, item.DecisionReason, item.Attendance, item.AttendanceAt);

    private static SessionCard Card(ScheduledSession item, int taken, SeatReservation? own)
    {
        var session = item.Booking;
        return new SessionCard(session.Id, item.Resource.Id, item.Resource.Name, item.Resource.Location, session.SessionTitle!,
            session.StartUtc, session.EndUtc, session.CancelledAt, session.SeatCapacity!.Value,
            session.CancelledAt is null ? Math.Max(0, session.SeatCapacity.Value - taken) : 0, session.SeatApprovalRequired,
            own is not null ? View(own) : null);
    }
}

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
        api.MapOrganizationStructure();
        api.MapOrganizationBranding();
        api.MapBookingPrices();
        api.MapCalendarImport();
        api.MapApprovalEndpoints();
        api.MapBookingForms();
        api.MapResourceSelections();
        api.MapBookingRouting();
        api.MapPost("/routing/book", BookRouted);
        api.MapGroupSessions();
        api.MapEventManagement();
        api.MapDiscoverSettings();
        api.MapAssistant();
        api.MapGet("/resources", GetResources);
        api.MapGet("/calendar", GetCalendar);
        api.MapGet("/resources/{resourceId}/slot-diagnostics", SlotDiagnostics.Check);
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
        api.MapGet("/bookings/{id:guid}/reschedule", RescheduleOptions);
        api.MapPost("/bookings/{id:guid}/reschedule", RescheduleBooking);
        api.MapPost("/resources/{resourceId}/series/preview", PreviewSeries);
        api.MapPost("/resources/{resourceId}/series", BookSeries);
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

    private static async Task<IResult> GetOrganizations(BookingDb db, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        var organizations = await OrganizationScope.ForUser(db, user, ct);
        var reviewing = await BookingRouting.ReviewingOrganizations(db, OwnerId(user), clock.GetUtcNow()).ToArrayAsync(ct);
        return Results.Ok(organizations.Select(item => item with
        {
            CanReviewAssigned = item.Role == OrganizationMembership.Reviewer || reviewing.Contains(item.Id)
        }));
    }

    private static async Task<IResult> GetResources(BookingDb db, OrganizationScope scope, CancellationToken ct) =>
        Results.Ok(await scope.Resources(db).AsNoTracking()
            .Where(resource => !resource.IsArchived)
            .OrderBy(resource => resource.SortOrder).ThenBy(resource => resource.Name).ThenBy(resource => resource.Id)
            .Select(resource => new
            {
                resource.Id, resource.Name, resource.Location, resource.Type, resource.Capacity,
                resource.Equipment, resource.RequiresApproval, resource.PriceMinor
            })
            .ToListAsync(ct));

    private static async Task<IResult> GetNotifications(string? timeZone, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!Schedule.TryZone(timeZone, out var viewer))
            return InvalidTimeZone();
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
                StartsAt = booking.StartUtc, EndsAt = booking.EndUtc,
                booking.HoldExpiresAt
            }
        ).Take(20).ToListAsync(ct);

        return Results.Ok(notifications.Select(notification => new
        {
            notification.EventId,
            notification.BookingId,
            notification.ResourceId,
            notification.ResourceName,
            notification.StartsAt, notification.EndsAt,
            notification.HoldExpiresAt,
            Week = Schedule.Monday(Schedule.LocalDate(notification.StartsAt, viewer))
        }));
    }

    private static async Task<IResult> GetCalendar(
        string resourceId, DateOnly? week, string? timeZone, int? durationMinutes, ClaimsPrincipal user, TimeProvider clock,
        BookingDb db, OrganizationScope scope, CancellationToken ct)
    {
        if (!Schedule.TryZone(timeZone, out var viewer))
            return InvalidTimeZone();
        var now = clock.GetUtcNow();
        var today = Schedule.LocalDate(now, viewer);
        var firstWeek = Schedule.Monday(today);
        var lastWeek = Schedule.Monday(Schedule.DisplayLastDay(now, viewer));
        var monday = week ?? firstWeek;
        if (monday < firstWeek || monday > lastWeek || monday.DayOfWeek != DayOfWeek.Monday)
            return Results.Problem(statusCode: 400, title: "Выберите понедельник в пределах календаря.");
        var (start, end) = Schedule.WeekEnvelope(monday);
        var ownerId = OwnerId(user);
        // Read the booking and queue position from the same snapshot during a handoff.
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resource = await scope.Resources(db).AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == resourceId && !item.IsArchived, ct);
        if (resource is null)
            return Results.NotFound();
        var schedule = Schedule.Read(resource);
        var policy = schedule.Policies;
        var external = await ExternalAvailability.Read(db, resourceId, now, ct);
        var limitReached = await policy.AtLimit(db, resourceId, ownerId, now, ct);
        var duration = durationMinutes ?? Schedule.DefaultDuration;
        if (!schedule.Rules.Allows(duration))
            return Results.Problem(statusCode: 400, title: "Для ресурса недоступна эта длительность.");
        var busyStart = start.AddHours(-1);
        var busyEnd = end.AddHours(3);
        var active = await db.Bookings.AsNoTracking()
            .Where(booking => booking.ResourceId == resourceId && booking.CancelledAt == null
                && booking.OccupiedUntilUtc > busyStart && booking.OccupiedFromUtc < busyEnd)
            .OrderBy(booking => booking.StartUtc).ToListAsync(ct);
        var exact = active.ToDictionary(booking => booking.StartUtc);
        var pending = db.WaitlistEntries.AsNoTracking()
            .Where(entry => entry.ResourceId == resourceId && entry.RemovedAt == null
                && entry.DurationMinutes == duration && entry.StartUtc > now && entry.StartUtc >= start && entry.StartUtc < end);
        var positions = await pending.Where(entry => entry.OwnerId == ownerId)
            .Select(entry => new
            {
                entry.StartUtc,
                entry.Id,
                Position = pending.Count(other => other.StartUtc == entry.StartUtc && other.Id <= entry.Id)
            })
            .ToDictionaryAsync(entry => entry.StartUtc, ct);
        await snapshot.CommitAsync(ct);

        var stored = active.Where(booking => booking.StartUtc <= now || booking.SeatCapacity != null
                || booking.EndUtc == booking.StartUtc.AddMinutes(duration))
            .ToLookup(booking => Schedule.LocalDate(booking.StartUtc, viewer));
        var scheduled = Schedule.WeekStarts(monday, viewer, schedule, now, duration).ToLookup(time => Schedule.LocalDate(time, viewer));
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = monday.AddDays(offset);
            var slots = scheduled[date].Union(stored[date].Select(booking => booking.StartUtc)).Order().Select(startsAt =>
            {
                var window = schedule.Rules.Window(startsAt, duration);
                exact.TryGetValue(startsAt, out var booking);
                var block = active.FirstOrDefault(item => item.MaintenanceReason is not null && item.Window.Overlaps(window));
                var group = booking?.SeatCapacity != null ? booking : null;
                var ownBooking = booking?.OwnerId == ownerId && group is null
                    && booking.MaintenanceReason is null && booking.EndUtc == window.EndsAt ? booking : null;
                var occupied = active.Any(item => item.Window.Overlaps(window));
                positions.TryGetValue(startsAt, out var entry);
                var restriction = startsAt > now ? policy.TimeRestriction(startsAt, now)
                    ?? (limitReached ? policy.LimitMessage : null) ?? (external.Stale ? ExternalAvailability.StaleMessage : null) : null;
                var status = startsAt <= now ? "past"
                    : ownBooking is not null ? ownBooking.ApprovalStatus == BookingApproval.Pending ? "pending"
                        : ownBooking.HoldExpiresAt is null ? "mine" : "held"
                    : group is not null ? "group"
                    : block is not null ? "maintenance"
                    : occupied ? "booked"
                    : !external.Stale && external.Conflict(window) != null ? "external"
                    : window.OccupiedFrom <= now ? "past" : restriction is not null ? "restricted" : "available";
                return new CalendarSlot(startsAt, status, status == "past" ? null : ownBooking?.Id, ownBooking?.HoldExpiresAt,
                    entry?.Id, entry?.Position, block?.MaintenanceReason, scope.IsAdmin ? block?.Id : null,
                    group?.EndUtc ?? (startsAt <= now && booking is not null ? booking.EndUtc : window.EndsAt),
                    block?.StartUtc, block?.EndUtc,
                    restriction, ownBooking is { HoldExpiresAt: null } && startsAt > now ? policy.Changes(startsAt, now) : null,
                    scope.IsAdmin && booking?.AnswersJson is not null ? booking.Id : null, ownBooking?.BundleId, group?.Id, group?.SessionTitle);
            });
            return new { date, slots };
        });

        return Results.Ok(new { today, week = monday, firstWeek, lastWeek, resource.RequiresApproval, durationMinutes = duration,
            resource.PriceMinor, PublicLinkId = await OrganizationStructure.AllowsLinks(db, resource.Id, ct) ? resource.PublicLinkId : null,
            durationOptions = schedule.Rules.Options, schedule.Rules.BufferBeforeMinutes, schedule.Rules.BufferAfterMinutes,
            lastDay = Schedule.DisplayLastDay(now, viewer), resourceTimeZone = schedule.ZoneId, policy,
            bookingMessage = external.Stale ? ExternalAvailability.StaleMessage : limitReached ? policy.LimitMessage : null,
            bookingForm = BookingForm.Read(resource), days });
    }

    internal static IResult InvalidTimeZone() => Results.Problem(statusCode: 400, title: "Выберите существующий часовой пояс.");

    private static Task<IResult> BookSlot(
        SlotRequest request, ClaimsPrincipal user, TimeProvider clock, BookingDb db,
        OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        ForRequestedSlot(request, clock, db, scope,
            slot => bookings.Book(slot, OwnerId(user), ct, duration: request.DurationMinutes ?? Schedule.DefaultDuration,
                submission: request.BookingForm, allowed: scope.Resources(db)), ct);

    private static Task<IResult> BookRouted(RoutedBookingInput input, ClaimsPrincipal user, TimeProvider clock, BookingDb db,
        OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        !BookingRouting.ValidAnswers(input.Answers) ? Task.FromResult(RoutingEndpoints.InvalidAnswers())
        : ForRequestedSlot(new SlotRequest(input.ResourceId, input.StartsAt), clock, db, scope,
            slot => bookings.Book(slot, OwnerId(user), ct, duration: input.DurationMinutes,
                submission: input.BookingForm, routing: input, allowed: scope.Resources(db)), ct);

    private static Task<IResult> JoinWaitlist(
        SlotRequest request, ClaimsPrincipal user, TimeProvider clock, BookingDb db,
        OrganizationScope scope, SlotBookings bookings, CancellationToken ct) =>
        ForRequestedSlot(request, clock, db, scope,
            slot => bookings.JoinWaitlist(slot, OwnerId(user), scope.Resources(db), ct,
                request.DurationMinutes ?? Schedule.DefaultDuration), ct);

    private static async Task<IResult> ForRequestedSlot(
        SlotRequest request, TimeProvider clock, BookingDb db, OrganizationScope scope,
        Func<SlotKey, Task<IResult>> action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 32
            || !Schedule.InBookableHorizon(request.StartsAt, clock.GetUtcNow()))
        {
            return Results.Problem(statusCode: 400, title: "Выберите будущий слот из календаря.");
        }

        if (!await scope.Resources(db).AnyAsync(resource => resource.Id == request.ResourceId, ct))
            return Results.Problem(statusCode: 404, title: "Ресурс не найден.");

        return await action(new SlotKey(request.ResourceId, request.StartsAt.ToUniversalTime()));
    }

    private static Task<IResult> CancelBooking(
        Guid id, ClaimsPrincipal user, OrganizationScope scope, BookingDb db, SlotBookings bookings, CancellationToken ct) =>
        bookings.Cancel(id, OwnerId(user), scope.Resources(db), ct);

    private static Task<IResult> PreviewSeries(string resourceId, WeeklySeriesInput input, ClaimsPrincipal user,
        OrganizationScope scope, BookingDb db, SlotBookings bookings, CancellationToken ct) =>
        bookings.PreviewSeries(scope.Resources(db).Where(item => item.Id == resourceId), OwnerId(user), input, ct);

    private static Task<IResult> BookSeries(string resourceId, WeeklySeriesInput input, ClaimsPrincipal user,
        OrganizationScope scope, BookingDb db, SlotBookings bookings, CancellationToken ct) =>
        bookings.BookSeries(scope.Resources(db).Where(item => item.Id == resourceId), OwnerId(user), input, ct);

    private static Task<IResult> RescheduleOptions(Guid id, ClaimsPrincipal user, OrganizationScope scope, BookingDb db,
        SlotBookings bookings, CancellationToken ct) => bookings.RescheduleOptions(id, OwnerId(user), scope.Resources(db), ct);

    private static Task<IResult> RescheduleBooking(Guid id, RescheduleInput input, ClaimsPrincipal user,
        OrganizationScope scope, BookingDb db, SlotBookings bookings, CancellationToken ct) =>
        bookings.Reschedule(id, OwnerId(user), scope.Resources(db), input.StartsAt, WorkspaceEndpoints.Actor(user), ct);

    private static Task<IResult> ConfirmHold(
        Guid id, BookingFormSubmission? submission, ClaimsPrincipal user, OrganizationScope scope, BookingDb db,
        SlotBookings bookings, CancellationToken ct) =>
        bookings.Confirm(id, OwnerId(user), scope.Resources(db), ct, submission);

    private static Task<IResult> LeaveWaitlist(
        long id, ClaimsPrincipal user, OrganizationScope scope, BookingDb db, SlotBookings bookings, CancellationToken ct) =>
        bookings.LeaveWaitlist(id, OwnerId(user), scope.Resources(db), ct);

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
            slot => bookings.BlockSlot(slot, OwnerId(user), WorkspaceEndpoints.Actor(user), reason, ct,
                request.DurationMinutes ?? Schedule.DefaultDuration), ct);
    }

    private static async Task<IResult> BlockWindow(MaintenanceWindowRequest request, ClaimsPrincipal user,
        TimeProvider clock, BookingDb db, OrganizationScope scope, SlotBookings bookings, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var reason = request.Reason?.Trim();
        var today = Schedule.LocalDate(clock.GetUtcNow());
        if (string.IsNullOrEmpty(reason) || reason.Length > 160 || request.Date < today.AddDays(-2)
            || request.Date > today.AddDays(BookingPolicy.MaximumDays + 2)
            || !TryMaintenanceTimes(request, out var from, out var through) || from > through)
            return Results.Problem(statusCode: 400, title: "Выберите часы работы ресурса и укажите причину до 160 символов.");

        if (!await scope.Resources(db).AnyAsync(resource => resource.Id == request.ResourceId, ct))
            return Results.Problem(statusCode: 404, title: "Ресурс не найден.");
        return await bookings.BlockWindow(request.ResourceId, request.Date, from, through, request.UntilTime is not null,
            OwnerId(user), WorkspaceEndpoints.Actor(user), reason, ct);
    }

    private static bool TryMaintenanceTimes(MaintenanceWindowRequest request, out TimeOnly from, out TimeOnly through)
    {
        from = through = default;
        if (request.FromTime is not null || request.ThroughTime is not null || request.UntilTime is not null)
            return Schedule.TryTime(request.FromTime, out from) && Schedule.TryTime(request.UntilTime ?? request.ThroughTime, out through);
        if (request.FromHour is not { } first || request.ThroughHour is not { } last
            || !Schedule.Hours.Contains(first) || !Schedule.Hours.Contains(last))
            return false;
        from = new TimeOnly(first, 0);
        through = new TimeOnly(last, 0);
        return true;
    }

    private static Task<IResult> UnblockSlot(Guid id, OrganizationScope scope, ClaimsPrincipal user,
        SlotBookings bookings, CancellationToken ct) =>
        scope.IsAdmin ? bookings.Unblock(id, scope.Current.Id, WorkspaceEndpoints.Actor(user), ct)
            : Task.FromResult(Results.StatusCode(403));

    private sealed record SlotRequest(string ResourceId, DateTimeOffset StartsAt, int? DurationMinutes = null,
        BookingFormSubmission? BookingForm = null);
    private sealed record MaintenanceRequest(string ResourceId, DateTimeOffset StartsAt, string? Reason, int? DurationMinutes);
    private sealed record MaintenanceWindowRequest(string ResourceId, DateOnly Date, int? FromHour, int? ThroughHour,
        string? Reason, string? FromTime, string? ThroughTime, string? UntilTime);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId,
        DateTimeOffset? HoldExpiresAt, long? WaitlistId, int? WaitlistPosition, string? MaintenanceReason, Guid? MaintenanceId,
        DateTimeOffset EndsAt, DateTimeOffset? MaintenanceStartsAt, DateTimeOffset? MaintenanceEndsAt,
        string? BookingRestriction, BookingChangeTimes? Changes, Guid? AnswerBookingId, Guid? BundleId,
        Guid? GroupSessionId, string? GroupTitle);
}

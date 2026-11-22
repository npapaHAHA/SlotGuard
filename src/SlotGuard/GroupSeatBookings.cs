using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed partial class SlotBookings
{
    public async Task<IResult> CreateGroupSession(IQueryable<Resource> allowed, GroupSessionInput input, Guid owner,
        string actor, CancellationToken ct)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 100 || title.Any(char.IsControl)
            || input.Seats is < 2 or > GroupSessionEndpoints.MaxSeats || !Schedule.Durations.Contains(input.DurationMinutes))
            return Problem(400, "Укажите название, длительность и число мест от 2 до 100.");
        var id = input.ResourceId;
        if (string.IsNullOrEmpty(id) || !await allowed.AsNoTracking().AnyAsync(item => item.Id == id && !item.IsArchived, ct))
            return Results.NotFound();

        return await InResource(id, async (now, resource, schedule) =>
        {
            if (resource.IsArchived)
                return ArchivedResource();
            if (resource.PriceMinor.HasValue)
                return BookingPayments.OnlyPublic();
            if (!GroupSessionEndpoints.CanHost(resource) || input.Seats > GroupSessionEndpoints.CapacityFor(resource))
                return Problem(422, "Число мест должно укладываться во вместимость подходящего помещения.");
            var start = input.StartsAt.ToUniversalTime();
            if (ValidateStart(start, now, schedule, input.DurationMinutes) is { } invalid)
                return invalid;
            var window = schedule.Rules.Window(start, input.DurationMinutes);
            if (await ExternalConflict(id, window, now, ct) is { } external)
                return external;
            if (await Overlapping(id, window).AnyAsync(ct))
                return Problem(409, "Это время уже занято.");

            var session = NewBooking(id, window, owner);
            session.SessionTitle = title;
            session.SeatCapacity = input.Seats;
            session.SeatApprovalRequired = input.RequiresApproval || resource.RequiresApproval;
            db.Bookings.Add(session);
            await WebhookEvents.Enqueue(db, resource.OrganizationId, session, WebhookEvents.Created, now, ct);
            Audit.Add(db, resource, actor, "group-session.created", $"{title} · {input.Seats} мест", now);
            return Results.Created($"/api/group-sessions/{session.Id}", new { session.Id });
        }, ct);
    }

    public Task<IResult> BookSeat(IQueryable<Resource> allowed, Guid id, Guid owner, BookingFormSubmission? form,
        CancellationToken ct, GuestRsvp? guest = null) =>
        InGroupSession(allowed, id, async (session, resource, schedule, now) =>
        {
            var registrations = db.SeatReservations.Where(item => item.SessionId == id);
            var seats = registrations.Active();
            var mine = await registrations.ActiveOrWaiting().AsNoTracking().SingleOrDefaultAsync(item => item.OwnerId == owner, ct);
            if (mine is not null && session.CancelledAt is null)
                return Results.Ok(GroupSessionEndpoints.View(mine));
            if (session.CancelledAt is not null || session.StartUtc <= now || resource.IsArchived)
                return Problem(409, "Запись на это занятие закрыта.");
            if (guest is not null)
            {
                var publication = await db.PublishedEvents.AsNoTracking().SingleOrDefaultAsync(item => item.SessionId == id, ct);
                if (publication?.IsOpen(now) != true)
                    return Problem(409, "Публичная регистрация закрыта.");
                var email = guest.Email.ToUpperInvariant();
                if (await registrations.ActiveOrWaiting().AnyAsync(item => item.GuestEmail != null && item.GuestEmail.ToUpper() == email, ct))
                    return Problem(409, "Этот адрес уже зарегистрирован.");
            }
            if (schedule.Policies.TimeRestriction(session.StartUtc, now) is { } reason)
                return PolicyConflict(reason);
            if (await ExternalConflict(resource.Id, session.Window, now, ct) is { } external)
                return external;
            var full = await seats.CountAsync(ct) >= session.SeatCapacity!.Value;
            if (full && guest is null)
                return Problem(409, "Все места на это занятие уже заняты.");
            if (full && await registrations.CountAsync(item => item.Status == SeatStatus.Waiting, ct) >= 100)
                return Problem(409, "Лист ожидания заполнен.");
            if (await schedule.Policies.AtLimit(db, resource.Id, owner, now, ct))
                return PolicyConflict(schedule.Policies.LimitMessage);
            if (BookingForm.Read(resource).Check(form, out var answers) is { } invalid)
                return invalid;

            var reservation = new SeatReservation
            {
                Id = Guid.NewGuid(), SessionId = id, OwnerId = owner, CreatedAt = now,
                Status = full ? SeatStatus.Waiting : session.SeatApprovalRequired ? SeatStatus.Pending : SeatStatus.Confirmed,
                AnswersJson = answers, GuestName = guest?.Name, GuestEmail = guest?.Email
            };
            db.SeatReservations.Add(reservation);
            EventMail.Queue(db, reservation, now);
            return Results.Json(GroupSessionEndpoints.View(reservation), statusCode: 201);
        }, ct);

    public Task<IResult> CancelSeat(IQueryable<Resource> allowed, Guid id, Guid owner, CancellationToken ct) =>
        InGroupSession(allowed, id, async (session, _, schedule, now) =>
        {
            var seat = await db.SeatReservations.Where(item => item.SessionId == id && item.OwnerId == owner)
                .ActiveOrWaiting().SingleOrDefaultAsync(ct);
            if (seat is null)
                return Results.NoContent();

            var changes = schedule.Policies.Changes(session.StartUtc, now);
            if (seat.Status != SeatStatus.Waiting && !changes.CanCancel)
                return PolicyConflict("Срок отмены места истёк.", changes.CancelUntil);
            var occupied = seat.Status != SeatStatus.Waiting;
            seat.Status = SeatStatus.Cancelled;
            if (occupied) await PromoteEventWaitlist(session, now, 1, ct);
            return Results.NoContent();
        }, ct);

    public Task<IResult> CancelGroupSession(IQueryable<Resource> allowed, Guid id, string actor, CancellationToken ct) =>
        InGroupSession(allowed, id, async (session, resource, schedule, now) =>
        {
            if (session.CancelledAt is not null)
                return Results.NoContent();
            if (session.StartUtc <= now)
                return Problem(409, "Занятие уже началось.");
            await db.SeatReservations.Where(item => item.SessionId == id).ActiveOrWaiting()
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, SeatStatus.Cancelled), ct);
            await Release([session], resource, schedule, now, ct);
            Audit.Add(db, resource, actor, "group-session.cancelled", session.SessionTitle!, now);
            return Results.NoContent();
        }, ct);

    public Task<IResult> DecideSeats(IQueryable<Resource> allowed, Guid id, SeatDecision input, string actor,
        CancellationToken ct)
    {
        var ids = input.SeatIds;
        var action = input.Action;
        var reason = input.Reason?.Trim();
        if (ids is not { Length: >= 1 and <= GroupSessionEndpoints.MaxSeats }
            || ids.Distinct().Count() != ids.Length || ids.Contains(Guid.Empty)
            || action is not ("confirm" or "reject" or SeatStatus.Present or SeatStatus.NoShow or "clear-attendance")
            || reason?.Length > 240 || reason?.Any(char.IsControl) == true
            || action == "reject" && string.IsNullOrWhiteSpace(reason))
            return Task.FromResult(Problem(400, "Выберите участников и действие. Для отказа укажите причину до 240 символов."));

        return InGroupSession(allowed, id, async (session, resource, _, now) =>
        {
            if (session.CancelledAt is not null)
                return Problem(409, "Занятие отменено.");
            var selected = await db.SeatReservations.Where(item => item.SessionId == id && ids.Contains(item.Id)).ToArrayAsync(ct);
            if (selected.Length != ids.Length)
                return Results.NotFound();

            if (action is "confirm" or "reject")
            {
                var target = action == "confirm" ? SeatStatus.Confirmed : SeatStatus.Rejected;
                if (session.StartUtc <= now || selected.Any(item => item.Status != SeatStatus.Pending && item.Status != target))
                    return Problem(409, "Одна из заявок уже изменилась или срок согласования истёк.");
                if (target == SeatStatus.Confirmed && selected.Any(item => item.Status == SeatStatus.Pending)
                    && await ExternalConflict(resource.Id, session.Window, now, ct) is { } external)
                    return external;

                var changed = selected.Where(item => item.Status == SeatStatus.Pending).ToArray();
                foreach (var seat in changed)
                {
                    seat.Status = target;
                    if (target == SeatStatus.Confirmed)
                        seat.WasApproved = true;
                    seat.DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
                    EventMail.Queue(db, seat, now);
                }
                if (target == SeatStatus.Rejected && changed.Length > 0)
                    await PromoteEventWaitlist(session, now, changed.Length, ct);
                if (changed.Length > 0)
                    Audit.Add(db, resource, actor, $"group-seat.{target}", $"{session.SessionTitle} · {changed.Length}", now);
            }
            else
            {
                if (selected.Any(item => item.Status != SeatStatus.Confirmed))
                    return Problem(409, "Посещаемость отмечается для подтверждённых участников.");
                if (action == SeatStatus.Present && now < session.StartUtc.AddMinutes(-15))
                    return Problem(422, "Приход можно отмечать за 15 минут до начала.");
                if (action == SeatStatus.NoShow && now < session.EndUtc)
                    return Problem(422, "Неявку можно отметить после окончания занятия.");

                var target = action == "clear-attendance" ? null : action;
                var changed = selected.Where(item => item.Attendance != target).ToArray();
                foreach (var seat in changed)
                {
                    seat.Attendance = target;
                    seat.AttendanceAt = target is null ? null : now;
                }
                if (changed.Length > 0)
                    Audit.Add(db, resource, actor, $"group-seat.{action}", $"{session.SessionTitle} · {changed.Length}", now);
            }
            return Results.NoContent();
        }, ct);
    }

    private async Task<IResult> InGroupSession(IQueryable<Resource> allowed, Guid id,
        Func<Booking, Resource, ResourceSchedule, DateTimeOffset, Task<IResult>> action, CancellationToken ct)
    {
        var visible = GroupSessionEndpoints.ScopedSessions(db, allowed).AsNoTracking();
        var resourceId = await visible.Where(item => item.Id == id).Select(item => item.ResourceId)
            .SingleOrDefaultAsync(ct);
        if (resourceId is null)
            return Results.NotFound();

        // All seat changes use the same resource lock as regular bookings and maintenance.
        return await InResource(resourceId, async (now, resource, schedule) =>
        {
            if (!await allowed.AnyAsync(item => item.Id == resourceId, ct))
                return Results.NotFound();
            var session = await db.Bookings.SingleAsync(item => item.Id == id, ct);
            return await action(session, resource, schedule, now);
        }, ct);
    }

    private async Task PromoteEventWaitlist(Booking session, DateTimeOffset now, int capacity, CancellationToken ct)
    {
        if (session.CancelledAt is not null || session.StartUtc <= now) return;
        var waiting = await db.SeatReservations.Where(item => item.SessionId == session.Id && item.Status == SeatStatus.Waiting)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(capacity).ToArrayAsync(ct);
        foreach (var entry in waiting)
        {
            entry.Status = session.SeatApprovalRequired ? SeatStatus.Pending : SeatStatus.Confirmed;
            EventMail.Queue(db, entry, now);
        }
    }

    public Task<IResult> AnnounceEvent(IQueryable<Resource> allowed, Guid id, string message, string actor, CancellationToken ct) =>
        InGroupSession(allowed, id, async (session, resource, _, now) =>
        {
            if (session.CancelledAt is not null || session.EndUtc <= now) return Problem(409, "Мероприятие завершено.");
            var attendees = await db.SeatReservations.AsNoTracking().Where(item => item.SessionId == id
                && item.Status == SeatStatus.Confirmed && item.GuestEmail != null).ToArrayAsync(ct);
            foreach (var seat in attendees) EventMail.Queue(db, seat, now, message);
            Audit.Add(db, resource, actor, "event.announcement", $"{attendees.Length} получателей", now);
            return Results.Ok(new { Count = attendees.Length });
        }, ct);
}

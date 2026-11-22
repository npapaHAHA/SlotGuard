using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;

namespace SlotGuard;

internal sealed class PublishedEvent
{
    public Guid SessionId { get; init; }
    public bool Enabled { get; set; }
    public string Description { get; set; } = "";
    public string? City { get; set; }
    public DateTimeOffset? OpensAt { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public Guid? CoHostId { get; set; }

    public bool IsOpen(DateTimeOffset now) => Enabled
        && (OpensAt is null || OpensAt <= now) && (ClosesAt is null || ClosesAt > now);
}

internal sealed record GuestRsvp(string Name, string Email);

internal static class PublicEvents
{
    public static void MapEvents(this WebApplication app)
    {
        app.MapGet("/events/{id:guid}", (IWebHostEnvironment env) => Results.File(
            Path.Combine(env.WebRootPath, "event.html"), "text/html; charset=utf-8")).ExcludeFromDescription();
        var events = app.MapGroup("/api/public/events/{id:guid}").AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        events.MapGet("", Read);
        events.MapPost("/rsvp", Join).RequireRateLimiting("public-form");
        events.MapDelete("/rsvp", Leave).RequireRateLimiting("public-booking");
        events.MapGet("/calendar.ics", Calendar);
    }

    public static void MapEventManagement(this RouteGroupBuilder api)
    {
        var events = api.MapGroup("/events");
        events.MapGet("", ListManaged);
        events.MapPut("/{id:guid}", Publish);
        events.MapPost("/{id:guid}/check-in", CheckIn);
        events.MapPost("/{id:guid}/announce", Announce).RequireRateLimiting("account");
    }

    internal static IQueryable<Resource> PublicResources(BookingDb db, Guid sessionId) =>
        OrganizationStructure.Shareable(db).Where(resource => !resource.IsArchived
            && db.Bookings.Any(session => session.Id == sessionId && session.ResourceId == resource.Id && session.SeatCapacity != null)
            && db.PublishedEvents.Any(item => item.SessionId == sessionId && item.Enabled));

    private static IQueryable<Resource> ManagedResources(BookingDb db, OrganizationScope scope, Guid sessionId) =>
        scope.Resources(db).Where(resource => db.Bookings.Any(item => item.Id == sessionId
                && item.ResourceId == resource.Id && item.SeatCapacity != null)
            && (scope.IsAdmin || db.PublishedEvents.Any(item => item.SessionId == sessionId && item.CoHostId == scope.OwnerId)));

    private static async Task<IResult> Read(Guid id, BookingDb db, HttpContext http, TimeProvider clock, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var info = await (from session in db.Bookings.AsNoTracking()
                          join resource in PublicResources(db, id).AsNoTracking() on session.ResourceId equals resource.Id
                          join settings in db.PublishedEvents.AsNoTracking() on session.Id equals settings.SessionId
                          join organization in db.Organizations on resource.OrganizationId equals organization.Id
                          where session.Id == id
                          select new { session, resource, settings, organization.Name }).SingleOrDefaultAsync(ct);
        if (info is null) return Results.NotFound();
        var owner = await PublicBookingEndpoints.ReadOwner(http);
        var attendees = db.SeatReservations.AsNoTracking().Where(item => item.SessionId == id);
        var mine = owner is null ? null : await attendees.Where(item => item.OwnerId == owner)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstOrDefaultAsync(ct);
        var used = await attendees.Active().CountAsync(ct);
        await snapshot.CommitAsync(ct);
        var now = clock.GetUtcNow();
        var booking = info.session;
        return Results.Ok(new
        {
            booking.SessionTitle, booking.StartUtc, booking.EndUtc, booking.CancelledAt, booking.SeatCapacity,
            Remaining = Math.Max(0, booking.SeatCapacity!.Value - used), info.resource.Location, Organization = info.Name,
            info.settings.Description, info.settings.City, info.settings.OpensAt, info.settings.ClosesAt,
            Open = booking.CancelledAt is null && booking.StartUtc > now && info.settings.IsOpen(now),
            Form = BookingForm.Read(info.resource),
            Mine = mine is null ? null : GroupSessionEndpoints.View(mine)
        });
    }

    private static async Task<IResult> Join(Guid id, RsvpInput input, BookingDb db, HttpContext http, SlotBookings bookings,
        CancellationToken ct)
    {
        if (Clean(input.Name, 80) is not { } name ||
            !PublicBookingEndpoints.TryEmail(input.Email, out var email) || email is null)
            return Invalid("Укажите имя и email для регистрации.");
        var owner = await PublicBookingEndpoints.EnsureOwner(http);
        return await bookings.BookSeat(PublicResources(db, id), id, owner, input.BookingForm, ct, new GuestRsvp(name, email));
    }

    private static async Task<IResult> Leave(Guid id, BookingDb db, HttpContext http, SlotBookings bookings, CancellationToken ct) =>
        await PublicBookingEndpoints.ReadOwner(http) is { } owner
            ? await bookings.CancelSeat(PublicResources(db, id), id, owner, ct) : Results.NotFound();

    private static async Task<IResult> Calendar(Guid id, BookingDb db, HttpContext http, TimeProvider clock, CancellationToken ct)
    {
        if (await PublicBookingEndpoints.ReadOwner(http) is not { } owner) return Results.NotFound();
        var view = await (from session in db.Bookings
                          join resource in PublicResources(db, id) on session.ResourceId equals resource.Id
                          where session.Id == id && session.CancelledAt == null
                            && db.SeatReservations.Any(item => item.SessionId == id && item.OwnerId == owner && item.Status == SeatStatus.Confirmed)
                          select new { session, resource }).SingleOrDefaultAsync(ct);
        return view is null ? Results.NotFound() : Results.File(
            BookingCalendar.Create(view.session, view.resource with { Name = view.session.SessionTitle! }, clock.GetUtcNow()),
            BookingCalendar.ContentType, BookingCalendar.FileName);
    }

    private static async Task<IResult> ListManaged(BookingDb db, OrganizationScope scope, HttpContext http,
        TimeProvider clock, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var now = clock.GetUtcNow();
        var sessions = from session in GroupSessionEndpoints.ScopedSessions(db, scope.Resources(db)).AsNoTracking()
                       join item in db.PublishedEvents.AsNoTracking() on session.Id equals item.SessionId into optional
                       from publication in optional.DefaultIfEmpty()
                       where session.EndUtc > now.AddDays(-30) && (scope.IsAdmin || publication != null && publication.CoHostId == scope.OwnerId)
                       orderby session.StartUtc descending
                       select new { session.Id, session.SessionTitle, session.StartUtc, session.ResourceId, session.CancelledAt, Publication = publication };
        var list = await sessions.Take(100).ToArrayAsync(ct);
        var members = scope.IsAdmin ? await (from member in db.Memberships.AsNoTracking()
                            join account in db.Users.AsNoTracking() on member.OwnerId equals account.Id into optional
                            from account in optional.DefaultIfEmpty()
                            where member.OrganizationId == scope.Current.Id
                            select new { Id = member.OwnerId, Name = account == null ? null : account.DisplayName }).ToArrayAsync(ct) : [];
        return Results.Ok(new { Sessions = list, CanPublish = scope.IsAdmin, Members = members.Select(item =>
            new { item.Id, Name = item.Name ?? DemoAccounts.LabelFor(item.Id) }) });
    }

    private static async Task<IResult> Publish(Guid id, PublishInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin) return Results.StatusCode(403);
        var description = input.Description?.Trim() ?? "";
        var city = Clean(input.City, 70);
        if (description.Length > 1200 || description.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
            || input.Enabled && city is null || input.OpensAt > input.ClosesAt)
            return Invalid("Укажите город, описание и корректное окно регистрации.");
        var target = await ManagedResources(db, scope, id).Select(resource => resource.Id).SingleOrDefaultAsync(ct);
        if (target is null) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, target, ct);
        if (resource is null) return Results.NotFound();
        var session = await db.Bookings.SingleAsync(item => item.Id == id, ct);
        if (input.Enabled && (resource.IsArchived || session.CancelledAt is not null || session.StartUtc <= clock.GetUtcNow()
            || input.OpensAt >= session.StartUtc || input.ClosesAt > session.StartUtc
            || !await OrganizationStructure.AllowsLinks(db, target, ct)))
            return Invalid("Опубликовать можно будущее занятие на открытом ресурсе. Регистрация заканчивается до начала.");
        if (input.CoHostId is { } host && !await db.Memberships.AnyAsync(item =>
            item.OrganizationId == scope.Current.Id && item.OwnerId == host, ct))
            return Invalid("Выберите соорганизатора из этой организации.");
        var entry = await db.PublishedEvents.SingleOrDefaultAsync(item => item.SessionId == id, ct);
        if (entry is null)
        {
            entry = new PublishedEvent { SessionId = id };
            db.PublishedEvents.Add(entry);
        }
        entry.Enabled = input.Enabled;
        entry.Description = description;
        entry.City = city;
        entry.OpensAt = input.OpensAt?.ToUniversalTime();
        entry.ClosesAt = input.ClosesAt?.ToUniversalTime();
        entry.CoHostId = input.CoHostId;
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "event.published",
            input.Enabled ? "Открыта публичная регистрация" : "Публичная регистрация закрыта", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static Task<IResult> CheckIn(Guid id, TicketInput input, BookingDb db, OrganizationScope scope, SlotBookings bookings,
        ClaimsPrincipal user, CancellationToken ct) =>
        bookings.DecideSeats(ManagedResources(db, scope, id), id,
            new SeatDecision([input.Ticket], SeatStatus.Present, null), WorkspaceEndpoints.Actor(user), ct);

    private static async Task<IResult> Announce(Guid id, MessageInput input, BookingDb db, OrganizationScope scope,
        TimeProvider clock, SlotBookings bookings, ClaimsPrincipal user, CancellationToken ct)
    {
        if (input.Message?.Trim() is not { Length: > 0 and <= 600 } message)
            return Invalid("Введите сообщение до 600 символов.");
        return await bookings.AnnounceEvent(ManagedResources(db, scope, id), id, message, WorkspaceEndpoints.Actor(user), ct);
    }

    internal static string? Clean(string? text, int max) =>
        text?.Trim() is { Length: > 0 } value && value.Length <= max && !value.Any(char.IsControl) ? value : null;
    private static IResult Invalid(string title) => Results.Problem(statusCode: 422, title: title);
    private sealed record RsvpInput(string? Name, string? Email, BookingFormSubmission? BookingForm);
    private sealed record PublishInput(bool Enabled, string? Description, string? City, DateTimeOffset? OpensAt,
        DateTimeOffset? ClosesAt, Guid? CoHostId);
    private sealed record TicketInput(Guid Ticket);
    private sealed record MessageInput(string? Message);
}

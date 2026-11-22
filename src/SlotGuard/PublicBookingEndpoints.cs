using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class PublicBookingEndpoints
{
    internal const string GuestScheme = "PublicBookingGuest";

    public static void MapPublicBookings(this WebApplication app)
    {
        app.MapGet("/book/{linkId:guid}", RenderPage).ExcludeFromDescription();
        var links = app.MapGroup("/api/public/resources/{linkId:guid}");
        links.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        links.MapPost("/session", StartSession).RequireRateLimiting("public-booking");
        links.MapPrivateVerification();
        MapBookingFlow(links);
    }

    internal static void MapBookingFlow(RouteGroupBuilder links)
    {
        links.MapGet("", Read);
        links.MapPost("/bookings", Book).RequireRateLimiting("public-form");
        links.MapPost("/bookings/{id:guid}/pay", Pay).RequireRateLimiting("public-booking");
        links.MapPost("/series/preview", PreviewSeries).RequireRateLimiting("public-booking");
        links.MapPost("/series", BookSeries).RequireRateLimiting("public-form");
        links.MapGet("/bookings/{id:guid}", ReadBooking);
        links.MapGet("/bookings/{id:guid}/calendar.ics", Calendar);
        links.MapDelete("/bookings/{id:guid}", CancelBooking).RequireRateLimiting("public-booking");
        links.MapGet("/bookings/{id:guid}/reschedule", RescheduleOptions);
        links.MapPost("/bookings/{id:guid}/reschedule", RescheduleBooking).RequireRateLimiting("public-booking");
    }

    private static async Task RenderPage(Guid linkId, BookingDb db, IWebHostEnvironment environment,
        HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!await ResourcesForLink(db, linkId).AnyAsync(ct))
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.SendFileAsync(Path.Combine(environment.WebRootPath, "book.html"), ct);
    }

    private static async Task<IResult> Read(Guid linkId, string? timeZone, int? durationMinutes, HttpContext context,
        BookingDb db, TimeProvider clock, PrivateLinkVerification verification, CancellationToken ct)
    {
        if (!Schedule.TryZone(timeZone, out var viewer))
            return BookingEndpoints.InvalidTimeZone();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resource = await VisibleResources(db, linkId, context).SingleOrDefaultAsync(ct);
        if (resource is null)
            return Results.NotFound();

        var appearance = (await db.Branding.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == resource.OrganizationId, ct))?.Public();
        var now = clock.GetUtcNow();
        var owner = await ReadOwner(context);
        var invitation = await db.PrivateBookingLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkId, ct);
        var verifiedEmail = invitation?.EmailDomain is not null ? verification.ReadAccess(context, linkId, owner) : null;
        var closure = invitation?.ClosedMessage(now);
        Guid? previous = null;
        if (invitation?.Check(now, verifiedEmail, appearance: appearance) is { } blocked)
        {
            if (closure is null || owner is null)
                return blocked;
            previous = await db.Bookings.AsNoTracking().Where(item => item.PrivateLinkId == linkId && item.OwnerId == owner)
                .OrderBy(item => item.CancelledAt != null).ThenByDescending(item => item.StartUtc)
                .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(ct);
            if (previous is null)
                return blocked;
        }
        var today = Schedule.LocalDate(now, viewer);
        var schedule = Schedule.Read(resource);
        var policy = schedule.Policies;
        var duration = durationMinutes ?? Schedule.DefaultDuration;
        if (!schedule.Rules.Allows(duration))
            return Results.Problem(statusCode: 400, title: "Выберите доступную длительность.");
        var atLimit = policy.MaxActiveBookings.HasValue && owner is { } ownerId
            && await policy.AtLimit(db, resource.Id, ownerId, now, ct);
        var guestLimit = owner is { } guest && await GuestBookingLimits.Reached(db, resource.Id, guest, 1, now, ct);
        var external = await ExternalAvailability.Read(db, resource.Id, now, ct);
        var slots = atLimit || guestLimit || closure is not null ? [] : await AvailabilitySearch.OpenStarts(db, resource.Id, schedule, duration, now, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new PublicResource(resource.Name, resource.Location, resource.Type, resource.Capacity,
            resource.Equipment, resource.RequiresApproval, resource.PublicPhotoUrl, today,
            Schedule.LocalDate(policy.Horizon(now).AddTicks(-1), viewer), slots, resource.TimeZone, duration, schedule.Rules.Options,
            policy, closure ?? (external.Stale ? ExternalAvailability.StaleMessage : atLimit ? policy.LimitMessage
                : guestLimit ? GuestBookingLimits.Message : null),
            BookingForm.Read(resource),
            invitation?.Remaining, verifiedEmail, previous, appearance, resource.PriceMinor));
    }

    private static async Task<IResult> Book(Guid linkId, PublicSlot input, BookingDb db, SlotBookings bookings,
        HttpContext context, TimeProvider clock, PrivateLinkVerification verification, CancellationToken ct)
    {
        var resourceId = await VisibleResources(db, linkId, context).Select(resource => resource.Id).SingleOrDefaultAsync(ct);
        if (resourceId is null)
            return Results.NotFound();
        if (!Schedule.InBookableHorizon(input.StartsAt, clock.GetUtcNow()))
            return Results.Problem(statusCode: 400, title: "Выберите будущий слот из календаря.");
        var embed = context.Features.Get<EmbedGuest>();
        if (embed is { Owner: null })
            return Results.Problem(statusCode: 401, title: "Обновите сессию бронирования.");
        if (!TryEmail(input.Email, out var email) || embed is not null && email is null)
            return Results.Problem(statusCode: 400, title: "Укажите корректный email для подтверждения.");

        var owner = await EnsureOwner(context);
        var duration = input.DurationMinutes ?? Schedule.DefaultDuration;
        var access = await LinkAccess(db, linkId, owner, context, verification, ct);
        var result = await bookings.Book(new SlotKey(resourceId, input.StartsAt.ToUniversalTime()), owner, ct,
            access is null ? linkId : null, duration, access?.Email ?? email, input.BookingForm, access, embed: embed?.Scope,
            expectedPriceMinor: input.ExpectedPriceMinor);
        if (result is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status409Conflict })
            return BookingReceipt(result);

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resource = await VisibleResources(db, linkId, context).SingleOrDefaultAsync(ct);
        if (resource is null)
            return Results.NotFound();
        var invitation = await db.PrivateBookingLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkId, ct);
        if (invitation?.Check(clock.GetUtcNow(), access?.Email) is { } blocked)
            return blocked;
        var starts = await AvailabilitySearch.OpenStarts(db, resource.Id, Schedule.Read(resource), duration, clock.GetUtcNow(), ct);
        await snapshot.CommitAsync(ct);
        var alternatives = Schedule.Nearest(starts, input.StartsAt).Take(6).ToArray();
        return Results.Problem(statusCode: 409, title: "Это время уже заняли.",
            extensions: new Dictionary<string, object?> { ["alternatives"] = alternatives });
    }

    private static async Task<IResult> PreviewSeries(Guid linkId, WeeklySeriesInput input, BookingDb db, SlotBookings bookings,
        HttpContext context, TimeProvider clock, PrivateLinkVerification verification, CancellationToken ct)
    {
        var visible = VisibleResources(db, linkId, context);
        if (!await visible.AnyAsync(ct))
            return Results.NotFound();
        var owner = await ReadOwner(context);
        var invitation = await db.PrivateBookingLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkId, ct);
        if (invitation?.Check(clock.GetUtcNow(), verification.ReadAccess(context, linkId, owner), input.Count) is { } blocked)
            return blocked;
        return await bookings.PreviewSeries(visible, owner ?? Guid.Empty, input, ct, guestManagement: true);
    }

    private static async Task<IResult> BookSeries(Guid linkId, WeeklySeriesInput input, BookingDb db, SlotBookings bookings,
        HttpContext context, PrivateLinkVerification verification, CancellationToken ct)
    {
        var embed = context.Features.Get<EmbedGuest>();
        if (embed is { Owner: null })
            return Results.Problem(statusCode: 401, title: "Обновите сессию бронирования.");
        if (!TryEmail(input.Email, out var email) || embed is not null && email is null)
            return Results.Problem(statusCode: 400, title: "Укажите корректный email для подтверждения.");
        if (!await VisibleResources(db, linkId, context).AnyAsync(ct))
            return Results.NotFound();
        var owner = await EnsureOwner(context);
        var access = await LinkAccess(db, linkId, owner, context, verification, ct);
        return await bookings.BookSeries(VisibleResources(db, linkId, context), owner, input, ct,
            guestManagement: true, guestEmail: access?.Email ?? email, privateAccess: access);
    }

    internal static bool TryEmail(string? input, out string? email)
    {
        email = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        return email is null || email.Length <= 254 && !email.Any(char.IsControl)
            && MailAddress.TryCreate(email, out var address) && address.Address == email;
    }

    private static async Task<IResult> ReadBooking(Guid linkId, Guid id, HttpContext context, BookingDb db,
        TimeProvider clock, CancellationToken ct)
    {
        if (await ReadOwner(context) is not { } owner)
            return Results.NotFound();
        var row = await (from item in db.Bookings.AsNoTracking()
            join resource in ReceiptResources(db, linkId, id, owner, context) on item.ResourceId equals resource.Id
            where item.Id == id && item.OwnerId == owner && item.MaintenanceReason == null
                && (item.HoldExpiresAt == null || item.PriceMinor != null)
            select new { Booking = item, Resource = resource }).SingleOrDefaultAsync(ct);
        if (row is null)
            return Results.NotFound();
        var booking = row.Booking;
        var next = await db.Bookings.AsNoTracking().Where(item => item.RescheduledFromId == id
            && item.ResourceId == booking.ResourceId && item.OwnerId == owner)
            .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(ct);
        return Results.Ok(Describe(booking, row.Resource, clock.GetUtcNow(), next));
    }

    internal static Receipt Describe(Booking booking, Resource resource, DateTimeOffset now, Guid? next = null)
    {
        var payment = BookingPayments.Describe(booking, now);
        var status = booking.ApprovalStatus switch
        {
            _ when next.HasValue => "rescheduled",
            _ when booking.PriceMinor is not null && booking.PaidAt is null => payment!.Status,
            BookingApproval.Rejected => BookingApproval.Rejected,
            _ when booking.CancelledAt is not null => "cancelled",
            BookingApproval.Pending when booking.StartUtc <= now => "expired",
            _ => booking.ApprovalStatus ?? "confirmed"
        };
        var changes = !resource.IsArchived && SlotBookings.CanReschedule(booking, now)
            ? Schedule.Read(resource).Policies.Changes(booking.StartUtc, now) : null;
        if (payment is { Status: BookingPayments.Awaiting, ExpiresAt: { } deadline })
            changes = new BookingChangeTimes(true, false, deadline, deadline);
        return new Receipt(booking.Id, booking.StartUtc, status, booking.ApprovalReason, booking.EndUtc,
            changes?.CanReschedule == true, next, changes, payment);
    }

    private static async Task<IResult> Pay(Guid linkId, Guid id, HttpContext context, BookingDb db, SlotBookings bookings,
        CancellationToken ct) => await ReadOwner(context) is { } owner
            ? await bookings.Pay(id, owner, RescheduleResources(db, linkId, id, owner, context), ct) : Results.NotFound();

    private static async Task<IResult> Calendar(Guid linkId, Guid id, HttpContext context, BookingDb db,
        TimeProvider clock, CancellationToken ct)
    {
        if (await ReadOwner(context) is not { } owner)
            return Results.NotFound();
        var row = await (from item in db.Bookings.AsNoTracking()
            join resource in ReceiptResources(db, linkId, id, owner, context) on item.ResourceId equals resource.Id
            where item.Id == id && item.OwnerId == owner
            select new { Booking = item, Resource = resource }).SingleOrDefaultAsync(ct);
        return row is null ? Results.NotFound() : BookingCalendar.Download(row.Booking, row.Resource, clock.GetUtcNow());
    }

    private static async Task<IResult> CancelBooking(Guid linkId, Guid id, HttpContext context, BookingDb db,
        SlotBookings bookings, CancellationToken ct) => await ReadOwner(context) is { } owner
            ? await bookings.Cancel(id, owner, ReceiptResources(db, linkId, id, owner, context), ct) : Results.NotFound();

    private static async Task<IResult> RescheduleOptions(Guid linkId, Guid id, HttpContext context, BookingDb db,
        SlotBookings bookings, CancellationToken ct) => await ReadOwner(context) is { } owner
            ? await bookings.RescheduleOptions(id, owner, RescheduleResources(db, linkId, id, owner, context), ct) : Results.NotFound();

    private static async Task<IResult> RescheduleBooking(Guid linkId, Guid id, RescheduleInput input,
        HttpContext context, BookingDb db, SlotBookings bookings, CancellationToken ct)
    {
        if (await ReadOwner(context) is not { } owner)
            return Results.NotFound();
        var result = await bookings.Reschedule(id, owner, RescheduleResources(db, linkId, id, owner, context), input.StartsAt, "Гость по ссылке", ct);
        return BookingReceipt(result);
    }

    private static IResult BookingReceipt(IResult result)
    {
        if (result is not IValueHttpResult { Value: CreatedBooking booking })
            return result;
        var awaiting = booking.Payment is { Status: BookingPayments.Awaiting, ExpiresAt: not null };
        var changes = awaiting && booking.Payment!.ExpiresAt is { } deadline
            ? new BookingChangeTimes(true, false, deadline, deadline) : booking.Changes;
        return Results.Json(new Receipt(booking.Id, booking.StartsAt,
            awaiting ? BookingPayments.Awaiting : booking.ApprovalStatus ?? "confirmed", null, booking.EndsAt,
            changes?.CanReschedule == true, Changes: changes, Payment: booking.Payment),
            statusCode: (result as IStatusCodeHttpResult)?.StatusCode);
    }

    private static async Task<IResult> StartSession(Guid linkId, HttpContext context, BookingDb db, CancellationToken ct)
    {
        if (!await ResourcesForLink(db, linkId).AnyAsync(ct))
            return Results.NotFound();
        await EnsureOwner(context);
        return Results.NoContent();
    }

    internal static async Task<Guid?> ReadOwner(HttpContext context)
    {
        if (context.Features.Get<EmbedGuest>() is { } embed)
            return embed.Owner;
        var guest = await context.AuthenticateAsync(GuestScheme);
        var principal = guest.Principal ?? context.User;
        return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    }

    internal static async Task<Guid> EnsureOwner(HttpContext context)
    {
        if (await ReadOwner(context) is { } owner)
            return owner;
        if (context.Features.Get<EmbedGuest>() is not null)
            throw new InvalidOperationException("An embedded booking requires an embed session.");
        var id = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], GuestScheme);
        await context.SignInAsync(GuestScheme, new ClaimsPrincipal(identity));
        return id;
    }

    private static IQueryable<Resource> ResourcesForLink(BookingDb db, Guid linkId) =>
        OrganizationStructure.Shareable(db).AsNoTracking().Where(resource => !resource.IsArchived && (resource.PublicLinkId == linkId
            || db.PrivateBookingLinks.Any(link => link.Id == linkId && link.ResourceId == resource.Id)));

    private static IQueryable<Resource> VisibleResources(BookingDb db, Guid linkId, HttpContext context) =>
        context.Features.Get<EmbedGuest>()?.Scope.Resources(db) ?? ResourcesForLink(db, linkId);

    private static IQueryable<Resource> ReceiptResources(BookingDb db, Guid linkId, Guid bookingId, Guid owner, HttpContext context) =>
        (context.Features.Get<EmbedGuest>()?.Scope.Resources(db) ?? db.Resources.AsNoTracking().Where(resource => !resource.IsArchived))
        .Where(resource => db.Bookings.Any(item => item.Id == bookingId && item.OwnerId == owner && item.ResourceId == resource.Id
            && item.SeatCapacity == null && (item.PrivateLinkId == linkId || item.PrivateLinkId == null && resource.PublicLinkId == linkId)));

    private static IQueryable<Resource> RescheduleResources(BookingDb db, Guid linkId, Guid bookingId, Guid owner, HttpContext context) =>
        ReceiptResources(db, linkId, bookingId, owner, context)
            .Where(resource => OrganizationStructure.Shareable(db).Any(item => item.Id == resource.Id));

    private static async Task<PrivateLinkAccess?> LinkAccess(BookingDb db, Guid id, Guid owner, HttpContext context,
        PrivateLinkVerification verification, CancellationToken ct) =>
        await db.PrivateBookingLinks.AsNoTracking().AnyAsync(item => item.Id == id, ct)
            ? new PrivateLinkAccess(id, verification.ReadAccess(context, id, owner)) : null;

    internal sealed record PublicResource(string Name, string Location, string Type, int? Capacity, string[] Equipment,
        bool RequiresApproval, string? PhotoUrl, DateOnly Today, DateOnly LastDay, DateTimeOffset[] Slots, string ResourceTimeZone,
        int DurationMinutes, int[] DurationOptions, BookingPolicy Policy, string? BookingMessage, BookingForm? BookingForm = null,
        int? RemainingUses = null, string? VerifiedEmail = null, Guid? PreviousBookingId = null, PublicAppearance? Appearance = null,
        int? PriceMinor = null);
    internal sealed record Receipt(Guid Id, DateTimeOffset StartsAt, string Status, string? Reason, DateTimeOffset EndsAt,
        bool CanReschedule, Guid? RescheduledToId = null, BookingChangeTimes? Changes = null, BookingPaymentView? Payment = null);
    private sealed record PublicSlot(DateTimeOffset StartsAt, int? DurationMinutes, string? Email, BookingFormSubmission? BookingForm,
        int? ExpectedPriceMinor = null);
}

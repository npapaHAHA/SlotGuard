using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class BookingManagementTokens(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("SlotGuard.BookingManagement.v1");

    public string Issue(Guid root) => protector.Protect(FormattableString.Invariant(
        $"{root:N}:{clock.GetUtcNow().AddDays(180).ToUnixTimeSeconds()}"));

    public Guid? Read(string token)
    {
        if (token.Length is 0 or > 512)
            return null;
        try
        {
            var parts = protector.Unprotect(token).Split(':');
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var root)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
                && expiry > clock.GetUtcNow().ToUnixTimeSeconds() ? root : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

internal static class BookingManagementEndpoints
{
    private const string TokenHeader = "X-SlotGuard-Booking-Token";

    public static void MapBookingManagement(this WebApplication app)
    {
        app.MapGet("/manage-booking", (HttpContext context, IWebHostEnvironment environment) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(Path.Combine(environment.WebRootPath, "manage-booking.html"), "text/html; charset=utf-8");
        }).ExcludeFromDescription();

        var management = app.MapGroup("/api/manage-booking");
        management.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        management.MapGet("", Read);
        management.MapGet("/calendar.ics", Calendar);
        management.MapDelete("/{id:guid}", Cancel).RequireRateLimiting("public-booking");
        management.MapGet("/{id:guid}/reschedule", RescheduleOptions);
        management.MapPost("/{id:guid}/reschedule", Reschedule).RequireRateLimiting("public-booking");
    }

    private static async Task<IResult> Read(HttpContext context, BookingManagementTokens tokens, BookingDb db,
        TimeProvider clock, CancellationToken ct)
    {
        var row = await Find(context, tokens, db, null, ct);
        if (row is null)
            return Results.NotFound();
        var booking = PublicBookingEndpoints.Describe(row.Booking, row.Resource, clock.GetUtcNow());
        if (booking.CanReschedule && !await RescheduleResourceScope(db, row).AnyAsync(ct))
            booking = booking with { CanReschedule = false };
        return Results.Ok(new
        {
            row.Resource.Name, row.Resource.Location, row.Resource.TimeZone,
            Booking = booking,
            CanDownload = BookingCalendar.IsConfirmed(row.Booking)
        });
    }

    private static async Task<IResult> Calendar(HttpContext context, BookingManagementTokens tokens, BookingDb db,
        TimeProvider clock, CancellationToken ct)
    {
        var row = await Find(context, tokens, db, null, ct);
        return row is null ? Results.NotFound() : BookingCalendar.Download(row.Booking, row.Resource, clock.GetUtcNow());
    }

    private static async Task<IResult> Cancel(Guid id, HttpContext context, BookingManagementTokens tokens,
        BookingDb db, SlotBookings bookings, CancellationToken ct)
    {
        var row = await Find(context, tokens, db, id, ct);
        return row is null ? Results.NotFound()
            : await bookings.Cancel(id, row.Booking.OwnerId, ResourceScope(db, row), ct);
    }

    private static async Task<IResult> RescheduleOptions(Guid id, HttpContext context, BookingManagementTokens tokens,
        BookingDb db, SlotBookings bookings, CancellationToken ct)
    {
        var row = await Find(context, tokens, db, id, ct);
        return row is null ? Results.NotFound()
            : await bookings.RescheduleOptions(id, row.Booking.OwnerId, RescheduleResourceScope(db, row), ct);
    }

    private static async Task<IResult> Reschedule(Guid id, RescheduleInput input, HttpContext context,
        BookingManagementTokens tokens, BookingDb db, SlotBookings bookings, CancellationToken ct)
    {
        var row = await Find(context, tokens, db, id, ct);
        return row is null ? Results.NotFound()
            : await bookings.Reschedule(id, row.Booking.OwnerId, RescheduleResourceScope(db, row), input.StartsAt, "Гость по письму", ct);
    }

    private static IQueryable<Resource> ResourceScope(BookingDb db, ManagedBooking row) =>
        db.Resources.Where(resource => resource.Id == row.Booking.ResourceId);

    private static IQueryable<Resource> RescheduleResourceScope(BookingDb db, ManagedBooking row) =>
        OrganizationStructure.Visible(db, ResourceScope(db, row), row.Booking.OwnerId);

    private static async Task<ManagedBooking?> Find(HttpContext context, BookingManagementTokens tokens, BookingDb db,
        Guid? id, CancellationToken ct)
    {
        if (tokens.Read(context.Request.Headers[TokenHeader].ToString()) is not { } root)
            return null;
        return await (from booking in db.Bookings.AsNoTracking()
            join resource in db.Resources.AsNoTracking() on booking.ResourceId equals resource.Id
            where booking.GuestManagement && (booking.Id == root || booking.CalendarRootId == root)
                && (!id.HasValue || booking.Id == id)
            orderby booking.CalendarSequence descending
            select new ManagedBooking(booking, resource)).FirstOrDefaultAsync(ct);
    }

    private sealed record ManagedBooking(Booking Booking, Resource Resource);
}

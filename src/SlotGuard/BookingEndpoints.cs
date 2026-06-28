using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SlotGuard;

internal static class BookingEndpoints
{
    public static void MapBookingEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/session", (Delegate)StartSession).AllowAnonymous();
        api.MapGet("/resources", GetResources);
        api.MapGet("/calendar", GetCalendar);
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
    }

    private static async Task<IResult> StartSession(HttpContext context)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            id = Guid.NewGuid();
            const string scheme = CookieAuthenticationDefaults.AuthenticationScheme;
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], scheme);
            await context.SignInAsync(scheme, new ClaimsPrincipal(identity));
        }

        return Results.Ok(new { label = $"Гость {id.ToString("N")[..4].ToUpperInvariant()}" });
    }

    private static async Task<IResult> GetResources(BookingDb db, CancellationToken ct) =>
        Results.Ok(await db.Resources.AsNoTracking()
            .OrderBy(resource => resource.SortOrder)
            .Select(resource => new { resource.Id, resource.Name, resource.Location })
            .ToListAsync(ct));

    private static async Task<IResult> GetCalendar(
        string resourceId, DateOnly? week, ClaimsPrincipal user, TimeProvider clock,
        BookingDb db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = Schedule.LocalDate(now);
        var firstWeek = Schedule.Monday(today);
        var lastWeek = firstWeek.AddDays(7 * (Schedule.WeekCount - 1));
        var monday = week ?? firstWeek;
        if (monday < firstWeek || monday > lastWeek || monday.DayOfWeek != DayOfWeek.Monday)
            return Results.Problem(statusCode: 400, title: "Выберите понедельник в пределах календаря.");
        if (!await db.Resources.AnyAsync(resource => resource.Id == resourceId, ct))
            return Results.NotFound();

        var start = Schedule.At(monday, 0);
        var end = Schedule.At(monday.AddDays(7), 0);
        var active = await db.Bookings.AsNoTracking()
            .Where(booking => booking.ResourceId == resourceId && booking.CancelledAt == null
                && booking.StartUtc >= start && booking.StartUtc < end)
            .ToDictionaryAsync(booking => booking.StartUtc, ct);
        var ownerId = OwnerId(user);

        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = monday.AddDays(offset);
            var slots = Schedule.Hours.Select(hour =>
            {
                var startsAt = Schedule.At(date, hour);
                active.TryGetValue(startsAt, out var booking);
                var isMine = booking?.OwnerId == ownerId;
                var status = startsAt <= now ? "past" : booking is null ? "available" : isMine ? "mine" : "booked";
                return new CalendarSlot(startsAt, status, isMine && status == "mine" ? booking!.Id : null);
            });
            return new { date, slots };
        });

        return Results.Ok(new { today, week = monday, firstWeek, lastWeek, days });
    }

    private static async Task<IResult> BookSlot(
        CreateBooking request, ClaimsPrincipal user, TimeProvider clock, BookingDb db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 32
            || !Schedule.CanBook(request.StartsAt, clock.GetUtcNow()))
        {
            return Results.Problem(statusCode: 400, title: "Выберите будущий слот из календаря.");
        }

        if (!await db.Resources.AnyAsync(resource => resource.Id == request.ResourceId, ct))
            return Results.Problem(statusCode: 404, title: "Ресурс не найден.");

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OwnerId = OwnerId(user),
            ResourceId = request.ResourceId,
            StartUtc = request.StartsAt.ToUniversalTime()
        };

        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: BookingDb.ActiveSlotIndex })
        {
            return Results.Problem(statusCode: 409, title: "Этот слот уже забронирован.");
        }

        return Results.Json(new CreatedBooking(booking.Id, booking.ResourceId, booking.StartUtc), statusCode: 201);
    }

    private static async Task<IResult> CancelBooking(
        Guid id, ClaimsPrincipal user, TimeProvider clock, BookingDb db, CancellationToken ct)
    {
        var ownerId = OwnerId(user);
        var now = clock.GetUtcNow();
        var affected = await db.Bookings
            .Where(booking => booking.Id == id && booking.OwnerId == ownerId
                && booking.CancelledAt == null && booking.StartUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(booking => booking.CancelledAt, now), ct);

        return affected == 1
            ? Results.NoContent()
            : Results.Problem(statusCode: 404, title: "Активная бронь не найдена.");
    }

    private static Guid OwnerId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private sealed record CreateBooking(string ResourceId, DateTimeOffset StartsAt);
    private sealed record CreatedBooking(Guid Id, string ResourceId, DateTimeOffset StartsAt);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId);
}

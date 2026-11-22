using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace SlotGuard;

internal sealed class ExternalApiAccess
{
    public ExternalApiKey Key { get; set; } = null!;
    public Guid Owner => OwnerOf(Key.OrganizationId);
    public Guid BookingId(Guid requestId) => Identifier($"SlotGuard.ExternalBooking:{Key.OrganizationId}:{requestId:D}");
    public static Guid OwnerOf(string organizationId) => Identifier($"SlotGuard.External:{organizationId}");
    private static Guid Identifier(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    public IQueryable<Resource> Resources(BookingDb db) => db.Resources.Where(item => item.OrganizationId == Key.OrganizationId);
}

internal sealed class ExternalApiFilter(BookingDb db, ExternalApiAccess access) : IEndpointFilter
{
    public const string KeyPrefix = "sgk_";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        var header = http.Request.Headers.Authorization.ToString();
        if (header.Length == 7 + KeyPrefix.Length + 64 && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && header.AsSpan(7).StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            var hash = WebhookEvents.HashKey(header[7..]);
            var key = await db.ExternalApiKeys.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TokenHash == hash && item.RevokedAt == null, http.RequestAborted);
            if (key is not null)
            {
                access.Key = key;
                return await next(context);
            }
        }
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: 401, title: "Use a valid SlotGuard API key.");
    }
}

internal static class ExternalApiEndpoints
{
    private sealed record CreateInput(string? ResourceId, DateTimeOffset StartsAt, int? DurationMinutes, BookingFormSubmission? BookingForm);
    private sealed record BookingPage(ExternalBooking[] Items, int? NextOffset);

    public static void DescribeExternalApi(OpenApiDocument document)
    {
        const string bearer = "SlotGuardApiKey";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[bearer] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Use an API key issued in SlotGuard Integrations."
        };
        foreach (var path in document.Paths.Where(item => item.Key.StartsWith("/api/v1/", StringComparison.Ordinal)))
            if (path.Value.Operations is { } operations)
                foreach (var operation in operations.Values)
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(bearer, document)] = [] }];
    }

    public static void MapExternalApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireRateLimiting("external-api").AddEndpointFilter<ExternalApiFilter>()
            .WithTags("External API v1");
        api.MapGet("/resources", Resources);
        api.MapGet("/resources/{id}/booking-form", Form);
        api.MapGet("/availability", Availability);
        api.MapGet("/bookings", List).Produces<BookingPage>();
        api.MapGet("/bookings/{id:guid}", Get).Produces<ExternalBooking>();
        api.MapPost("/bookings", Create).Produces<ExternalBooking>(201).Produces<ExternalBooking>(200);
        api.MapGet("/bookings/{id:guid}/reschedule", MoveOptions);
        api.MapPost("/bookings/{id:guid}/reschedule", Move).Produces<ExternalBooking>(201).Produces<ExternalBooking>(200);
        api.MapDelete("/bookings/{id:guid}", Cancel);
    }

    private static async Task<IResult> Resources(BookingDb db, ExternalApiAccess access, CancellationToken ct)
    {
        var resources = await access.Resources(db).AsNoTracking().Where(item => !item.IsArchived)
            .OrderBy(item => item.Name).ThenBy(item => item.Id).ToArrayAsync(ct);
        return Results.Ok(resources.Select(item => new
        {
            item.Id, item.Name, item.Location, item.Type, item.Capacity, item.Equipment, item.RequiresApproval, item.TimeZone,
            Durations = Schedule.Read(item).Rules.Options
        }));
    }

    private static async Task<IResult> Form(string id, BookingDb db, ExternalApiAccess access, CancellationToken ct)
    {
        var resource = await access.Resources(db).AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsArchived, ct);
        return resource is null ? Results.NotFound() : Results.Ok(BookingForm.Read(resource));
    }

    private static async Task<IResult> Availability(string resourceId, DateOnly date, string? timeZone, int? durationMinutes,
        BookingDb db, ExternalApiAccess access, TimeProvider clock, CancellationToken ct)
    {
        if (!Schedule.TryZone(timeZone, out var zone))
            return BookingEndpoints.InvalidTimeZone();
        var now = clock.GetUtcNow();
        if (date < Schedule.LocalDate(now, zone) || date > Schedule.DisplayLastDay(now, zone))
            return Results.Problem(statusCode: 400, title: "Choose a date within the booking horizon.");

        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var resource = await access.Resources(db).AsNoTracking().SingleOrDefaultAsync(item => item.Id == resourceId && !item.IsArchived, ct);
        if (resource is null)
            return Results.NotFound();
        var schedule = Schedule.Read(resource);
        var duration = durationMinutes ?? Schedule.DefaultDuration;
        if (!schedule.Rules.Allows(duration))
            return Results.Problem(statusCode: 400, title: "This resource does not support the requested duration.");
        var atLimit = await schedule.Policies.AtLimit(db, resourceId, access.Owner, now, ct);
        var starts = atLimit ? [] : await AvailabilitySearch.OpenStarts(db, resourceId, schedule, duration, now, ct);
        await snapshot.CommitAsync(ct);
        return Results.Ok(new
        {
            resource.Id, Date = date, TimeZone = zone.Id, DurationMinutes = duration, resource.RequiresApproval,
            Slots = starts.Where(start => Schedule.LocalDate(start, zone) == date)
                .Select(start => new { StartsAt = start, EndsAt = start.AddMinutes(duration) })
        });
    }

    private static async Task<IResult> List(DateOnly? from, DateOnly? through, string? resourceId, int? offset, int? limit,
        BookingDb db, ExternalApiAccess access, TimeProvider clock, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var first = from ?? today;
        var last = through ?? first.AddDays(first.Year >= 9999 ? 0 : 29);
        var take = limit ?? 50;
        var skip = offset ?? 0;
        if (first.Year < 2000 || last.Year > 9998 || last < first || last.DayNumber - first.DayNumber >= 93
            || take is < 1 or > 100 || skip is < 0 or > int.MaxValue - 100)
            return Results.Problem(statusCode: 400, title: "Choose up to 93 UTC dates, with limit between 1 and 100.");
        var start = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var query = Scoped(db, access).Where(item => item.StartUtc >= start && item.StartUtc < end);
        if (resourceId is not null)
        {
            if (!await access.Resources(db).AnyAsync(item => item.Id == resourceId, ct))
                return Results.NotFound();
            query = query.Where(item => item.ResourceId == resourceId);
        }
        var rows = await query.OrderBy(item => item.StartUtc).ThenBy(item => item.Id).Skip(skip).Take(take + 1).ToArrayAsync(ct);
        return Results.Ok(new BookingPage(rows.Take(take).Select(ExternalBooking.From).ToArray(),
            rows.Length > take ? skip + take : null));
    }

    private static async Task<IResult> Get(Guid id, BookingDb db, ExternalApiAccess access, CancellationToken ct)
    {
        var booking = await Scoped(db, access).SingleOrDefaultAsync(item => item.Id == id, ct);
        return booking is null ? Results.NotFound() : Results.Ok(ExternalBooking.From(booking));
    }

    private static async Task<IResult> Create(CreateInput input, HttpContext context, BookingDb db, ExternalApiAccess access,
        SlotBookings bookings, CancellationToken ct)
    {
        if (!access.Key.CanWrite)
            return ReadOnly();
        if (input.ResourceId is not { Length: > 0 and <= 32 } id || input.StartsAt == default)
            return Results.Problem(statusCode: 400, title: "Provide a resource and the slot start.");
        Guid? requestId = null;
        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var key))
        {
            if (!Guid.TryParse(key.ToString(), out var parsed) || parsed == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Idempotency-Key must be a UUID.");
            requestId = access.BookingId(parsed);
        }
        if (!await access.Resources(db).AnyAsync(item => item.Id == id && !item.IsArchived, ct))
            return Results.NotFound();
        var result = await bookings.Book(new SlotKey(id, input.StartsAt.ToUniversalTime()), access.Owner, ct,
            duration: input.DurationMinutes ?? Schedule.DefaultDuration, submission: input.BookingForm, externalRequestId: requestId);
        return BookingResponse(result);
    }

    private static Task<IResult> MoveOptions(Guid id, BookingDb db, ExternalApiAccess access, SlotBookings bookings, CancellationToken ct) =>
        access.Key.CanWrite ? bookings.RescheduleOptions(id, access.Owner, access.Resources(db), ct) : Task.FromResult(ReadOnly());

    private static async Task<IResult> Move(Guid id, RescheduleInput input, BookingDb db, ExternalApiAccess access,
        SlotBookings bookings, CancellationToken ct)
    {
        if (!access.Key.CanWrite)
            return ReadOnly();
        var result = await bookings.Reschedule(id, access.Owner, access.Resources(db), input.StartsAt, $"API · {access.Key.Name}", ct);
        return BookingResponse(result, id);
    }

    private static IResult BookingResponse(IResult result, Guid? previousId = null)
    {
        if (result is not IValueHttpResult { Value: CreatedBooking booking })
            return result;
        var previous = previousId == booking.Id ? null : previousId;
        var view = new ExternalBooking(booking.Id, booking.ResourceId, booking.StartsAt, booking.EndsAt,
            booking.ApprovalStatus == BookingApproval.Pending ? "pending" : "confirmed", previous, booking.BundleId, null, false);
        return Results.Json(view, statusCode: (result as IStatusCodeHttpResult)?.StatusCode ?? 200);
    }

    private static Task<IResult> Cancel(Guid id, BookingDb db, ExternalApiAccess access, SlotBookings bookings, CancellationToken ct) =>
        access.Key.CanWrite ? bookings.Cancel(id, access.Owner, access.Resources(db), ct) : Task.FromResult(ReadOnly());

    private static IQueryable<Booking> Scoped(BookingDb db, ExternalApiAccess access) =>
        db.Bookings.AsNoTracking().Where(item => item.MaintenanceReason == null && item.HoldExpiresAt == null
            && access.Resources(db).Any(resource => resource.Id == item.ResourceId));

    private static IResult ReadOnly() => Results.Problem(statusCode: 403, title: "This key only has read access.");
}

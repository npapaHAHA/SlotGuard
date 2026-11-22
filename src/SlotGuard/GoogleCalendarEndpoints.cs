using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SlotGuard;

internal static class GoogleCalendarEndpoints
{
    private const string CallbackPath = "/google-calendar/callback";
    private const string CorrelationCookie = "SlotGuard.GoogleCalendar";
    private const string StatePurpose = "SlotGuard.GoogleCalendar.State";
    internal const string TokenPurpose = "SlotGuard.GoogleCalendar.RefreshToken";

    private sealed record ConnectInput(string? CalendarId);
    private sealed record ConnectState(string ResourceId, string OrganizationId, Guid OwnerId, string CalendarId,
        string Nonce, string Verifier, DateTimeOffset ExpiresAt);

    public static void MapGoogleCalendarSettings(this RouteGroupBuilder workspace)
    {
        var endpoints = workspace.MapGroup("/resources/{id}/google-calendar");
        endpoints.MapGet("", Status);
        endpoints.MapPost("/connect", Start);
        endpoints.MapDelete("", Disconnect);
    }

    public static void MapGoogleCalendarCallback(this WebApplication app) =>
        app.MapGet(CallbackPath, Complete).ExcludeFromDescription();

    private static async Task<IResult> Status(string id, BookingDb db, OrganizationScope scope, TimeProvider clock,
        IOptions<GoogleCalendarOptions> options, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!await scope.Resources(db).AnyAsync(resource => resource.Id == id && !resource.IsArchived, ct))
            return Results.NotFound();
        var link = await db.GoogleCalendars.AsNoTracking().SingleOrDefaultAsync(item => item.ResourceId == id, ct);
        var now = clock.GetUtcNow();
        var connected = link?.ProtectedRefreshToken is not null;
        var availability = ExternalAvailability.From(link, now);
        var conflicts = 0;
        if (availability.Periods.Length > 0)
        {
            var bookings = await db.Bookings.AsNoTracking().Where(item => item.ResourceId == id && item.EndUtc > now
                && item.CancelledAt == null && item.MaintenanceReason == null).ToArrayAsync(ct);
            conflicts = bookings.Count(item => availability.Periods.Any(period => period.Intersects(item.Window)));
        }
        return Results.Ok(new
        {
            options.Value.Configured, Connected = connected, AvailabilityStale = availability.Stale,
            CalendarId = connected ? link!.CalendarId : null, Name = connected ? link!.Name : null,
            ImportedAt = connected ? link!.ImportedAt : null, ExportedAt = connected ? link!.ExportedAt : null,
            Error = connected ? link!.Error : null, Conflicts = conflicts
        });
    }

    private static async Task<IResult> Start(string id, ConnectInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, HttpContext context, IDataProtectionProvider protection,
        IOptions<GoogleCalendarOptions> options, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var settings = options.Value;
        if (!settings.Configured)
            return Results.Problem(statusCode: 503, title: "OAuth для Google Calendar пока не настроен.");
        var calendar = input.CalendarId?.Trim();
        if (string.IsNullOrEmpty(calendar) || calendar.Length > 254 || calendar.Any(char.IsControl))
            return Results.Problem(statusCode: 400, title: "Укажите ID календаря Google.");
        if (!await scope.Resources(db).AnyAsync(item => item.Id == id && !item.IsArchived, ct))
            return Results.NotFound();
        var callback = new Uri(settings.RedirectUri);
        if (!string.Equals(callback.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: 400, title: "Откройте SlotGuard по адресу, указанному для OAuth.");

        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var state = new ConnectState(id, scope.Current.Id, DemoAccounts.OwnerId(user), calendar, nonce, verifier,
            clock.GetUtcNow().AddMinutes(10));
        var sealedState = protection.CreateProtector(StatePurpose).Protect(JsonSerializer.Serialize(state));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Cookies.Append(CorrelationCookie, nonce, new CookieOptions
        {
            HttpOnly = true, Secure = callback.Scheme == "https", SameSite = SameSiteMode.Lax, Path = CallbackPath,
            MaxAge = TimeSpan.FromMinutes(10)
        });
        var authorize = QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
        {
            ["client_id"] = settings.ClientId, ["redirect_uri"] = settings.RedirectUri, ["response_type"] = "code",
            ["access_type"] = "offline", ["prompt"] = "consent",
            ["scope"] = "https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events",
            ["state"] = sealedState, ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        });
        return Results.Ok(new { Authorize = authorize });
    }

    private static async Task<IResult> Complete(string? state, string? code, HttpContext context, BookingDb db,
        TimeProvider clock, IDataProtectionProvider protection, GoogleCalendarClient client, CalendarUpdates updates,
        IOptions<GoogleCalendarOptions> options, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Cookies.Delete(CorrelationCookie, new CookieOptions { Path = CallbackPath });
        if (!options.Value.Configured || state is not { Length: > 0 and <= 4000 } || code is not { Length: > 0 and <= 1000 }
            || !context.Request.Cookies.TryGetValue(CorrelationCookie, out var nonce))
            return ReturnToWorkspace("denied");
        try
        {
            var request = JsonSerializer.Deserialize<ConnectState>(protection.CreateProtector(StatePurpose).Unprotect(state));
            if (request is null || request.Nonce != nonce || request.ExpiresAt <= clock.GetUtcNow())
                return ReturnToWorkspace("denied");
            if (!await IsAdmin(db, request, ct))
                return ReturnToWorkspace("denied");
            var token = await client.Exchange(code, request.Verifier, ct);
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
                return ReturnToWorkspace("token");
            var calendar = await client.Calendar(token.AccessToken, request.CalendarId, ct);
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(calendar.TimeZone, out _))
                return ReturnToWorkspace("calendar");

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var scope = new OrganizationScope
            {
                Current = new OrganizationAccess(request.OrganizationId, "", OrganizationMembership.Admin)
            };
            var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, request.ResourceId, ct);
            if (resource is null || resource.IsArchived || !await IsAdmin(db, request, ct))
                return ReturnToWorkspace("denied");
            var link = await db.GoogleCalendars.SingleOrDefaultAsync(item => item.ResourceId == resource.Id, ct);
            if (link?.ProtectedRefreshToken is not null && link.CalendarId != calendar.Id)
                return ReturnToWorkspace("replace");
            if (link is null)
            {
                link = new GoogleCalendarLink
                {
                    ResourceId = resource.Id, Key = Guid.NewGuid(), CalendarId = calendar.Id, Name = calendar.Summary,
                    TimeZone = calendar.TimeZone
                };
                db.GoogleCalendars.Add(link);
            }
            if (link.CalendarId != calendar.Id)
                link.Key = Guid.NewGuid();
            link.CalendarId = calendar.Id;
            link.Name = calendar.Summary;
            link.TimeZone = calendar.TimeZone;
            link.Revision = Guid.NewGuid();
            link.ProtectedRefreshToken = protection.CreateProtector(TokenPurpose).Protect(token.RefreshToken);
            link.BusyJson = "[]";
            link.ImportedAt = null;
            link.ExportedAt = null;
            link.NextSyncAt = clock.GetUtcNow();
            link.Error = null;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await updates.Publish(resource.OrganizationId);
            return ReturnToWorkspace("connected");
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { ConstraintName: "IX_GoogleCalendars_CalendarId" })
        {
            return ReturnToWorkspace("in-use");
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is
            (GoogleCalendarException or HttpRequestException or OperationCanceledException or CryptographicException
            or JsonException or FormatException))
        {
            return ReturnToWorkspace("calendar");
        }
    }

    private static async Task<IResult> Disconnect(string id, BookingDb db, OrganizationScope scope, CalendarUpdates updates,
        SlotBookings bookings, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Results.NotFound();
        var link = await db.GoogleCalendars.SingleOrDefaultAsync(item => item.ResourceId == id, ct);
        if (link is not null)
        {
            link.ProtectedRefreshToken = null;
            link.Revision = Guid.NewGuid();
            link.BusyJson = "[]";
            link.ImportedAt = null;
            link.ExportedAt = null;
            link.Error = null;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        await updates.Publish(resource.OrganizationId);
        if (!resource.IsArchived)
            await bookings.ReevaluateWaitlist(id, ct);
        return Results.NoContent();
    }

    private static Task<bool> IsAdmin(BookingDb db, ConnectState request, CancellationToken ct) =>
        db.Memberships.AnyAsync(item => item.OwnerId == request.OwnerId && item.OrganizationId == request.OrganizationId
            && item.Role == OrganizationMembership.Admin, ct);

    private static IResult ReturnToWorkspace(string status) => Results.LocalRedirect($"/?googleCalendar={status}#manage");
}

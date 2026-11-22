using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record EmbedScope(Guid LinkId, string? Parent)
{
    public bool Allows(Resource resource) => !resource.IsArchived && resource.PublicLinkId == LinkId
        && resource.EmbedOrigins.Length > 0 && (Parent is null || resource.EmbedOrigins.Contains(Parent, StringComparer.Ordinal));

    public IQueryable<Resource> Resources(BookingDb db) => OrganizationStructure.Shareable(db).AsNoTracking()
        .Where(item => !item.IsArchived && item.PublicLinkId == LinkId && item.EmbedOrigins.Length > 0
            && (Parent == null || item.EmbedOrigins.Contains(Parent)));
}

internal sealed record EmbedGuest(EmbedScope Scope, Guid? Owner);

internal sealed class EmbedSessions(IDataProtectionProvider provider, TimeProvider clock)
{
    internal const string ParentHeader = "X-SlotGuard-Embed-Origin";
    internal const string SessionHeader = "X-SlotGuard-Embed-Session";
    private readonly IDataProtector protection = provider.CreateProtector("SlotGuard.EmbeddedBooking.v1");
    private sealed record Grant(Guid Link, string? Parent, Guid Owner, DateTimeOffset ExpiresAt);

    public string Issue(EmbedScope scope, Guid owner) => protection.Protect(JsonSerializer.Serialize(
        new Grant(scope.LinkId, scope.Parent, owner, clock.GetUtcNow().AddHours(24))));

    public Guid? Read(EmbedScope scope, string? token)
    {
        if (token is not { Length: > 0 and <= 3200 })
            return null;
        try
        {
            var grant = JsonSerializer.Deserialize<Grant>(protection.Unprotect(token));
            return grant?.Link == scope.LinkId && grant.Parent == scope.Parent && grant.ExpiresAt > clock.GetUtcNow()
                ? grant.Owner : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}

internal static class EmbedOrigin
{
    public static string? Normalize(string? value)
    {
        if (value is not { Length: > 0 and <= 300 } || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            return null;
        string host;
        try { host = uri.IdnHost; }
        catch (UriFormatException) { return null; }
        var loopback = host == "localhost"
            || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
        if (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !loopback))
            return null;
        if (uri.HostNameType == UriHostNameType.IPv6)
            host = $"[{host.Trim('[', ']')}]";
        return $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
    }
}

internal sealed class EmbedAccessFilter(BookingDb db, EmbedSessions sessions) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.Request.RouteValues["linkId"]?.ToString(), out var linkId))
            return Results.NotFound();
        var raw = http.Request.Headers[EmbedSessions.ParentHeader].ToString();
        var parent = EmbedOrigin.Normalize(raw);
        if (raw.Length != 0 && parent is null)
            return Results.NotFound();
        var scope = new EmbedScope(linkId, parent);
        if (!await scope.Resources(db).AnyAsync(http.RequestAborted))
            return Results.NotFound();

        var owner = sessions.Read(scope, http.Request.Headers[EmbedSessions.SessionHeader]);
        http.Features.Set(new EmbedGuest(scope, owner));
        return await next(context);
    }
}

internal static class EmbedEndpoints
{
    private sealed record Settings(string[]? Origins);

    public static void MapEmbeds(this WebApplication app)
    {
        app.MapGet("/embed/{linkId:guid}", Page).ExcludeFromDescription();
        var api = app.MapGroup("/api/embed/resources/{linkId:guid}").AddEndpointFilter<EmbedAccessFilter>();
        api.MapPost("/session", Session).RequireRateLimiting("public-booking");
        PublicBookingEndpoints.MapBookingFlow(api);
    }

    public static void MapEmbedSettings(this RouteGroupBuilder workspace)
    {
        var embed = workspace.MapGroup("/resources/{id}/embed");
        embed.MapGet("", Read);
        embed.MapPut("", Save);
    }

    private static async Task<IResult> Page(Guid linkId, string? origin, BookingDb db, IWebHostEnvironment environment,
        HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var parent = EmbedOrigin.Normalize(origin);
        if (origin is not null && parent is null || !await new EmbedScope(linkId, parent).Resources(db).AnyAsync(ct))
            return Results.Content("Виджет SlotGuard недоступен для этого сайта.", "text/plain; charset=utf-8", statusCode: 404);
        context.Response.Headers.ContentSecurityPolicy = parent is null
            ? "frame-ancestors 'self'" : $"frame-ancestors 'self' {parent}";
        return Results.File(Path.Combine(environment.WebRootPath, "book.html"), "text/html; charset=utf-8");
    }

    private static IResult Session(HttpContext context, EmbedSessions sessions)
    {
        var guest = context.Features.Get<EmbedGuest>()!;
        return Results.Ok(new { Session = sessions.Issue(guest.Scope, guest.Owner ?? Guid.NewGuid()) });
    }

    private static async Task<IResult> Read(string id, BookingDb db, OrganizationScope scope, HttpContext context, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        context.Response.Headers.CacheControl = "no-store";
        var resource = await scope.Resources(db).AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsArchived, ct);
        return resource is null ? Results.NotFound() : View(resource.PublicLinkId, resource.EmbedOrigins);
    }

    private static async Task<IResult> Save(string id, Settings input, BookingDb db, OrganizationScope scope,
        System.Security.Claims.ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (input.Origins is not { Length: <= 12 })
            return InvalidOrigins();
        var candidates = input.Origins.Select(EmbedOrigin.Normalize).ToArray();
        if (candidates.Any(item => item is null))
            return InvalidOrigins();
        var origins = candidates.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null || resource.IsArchived)
            return Results.NotFound();
        if (origins.Length > 0 && resource.PublicLinkId is null)
            return Results.Problem(statusCode: 409, title: "Сначала включите публичную ссылку на ресурс.");
        if (origins.Length > 0 && !await OrganizationStructure.AllowsLinks(db, id, ct))
            return OrganizationStructure.LinksDisabled();
        if (!origins.SequenceEqual(resource.EmbedOrigins))
        {
            await scope.Resources(db).Where(item => item.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.EmbedOrigins, origins), ct);
            Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "resource.embed",
                origins.Length == 0 ? "Виджет отключён" : $"Разрешённых сайтов · {origins.Length}", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return View(resource.PublicLinkId, origins);
    }

    private static IResult View(Guid? publicLinkId, string[] origins) =>
        Results.Ok(new { PublicLinkId = publicLinkId, Origins = origins });

    private static IResult InvalidOrigins() =>
        Results.Problem(statusCode: 400, title: "Укажите до 12 адресов вида https://example.com. HTTP доступен для localhost.");
}

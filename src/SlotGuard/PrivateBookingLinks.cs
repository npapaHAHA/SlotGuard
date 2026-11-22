using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class PrivateBookingLink
{
    public Guid Id { get; init; }
    public required string ResourceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public int MaxUses { get; init; }
    public int UsedCount { get; set; }
    public string? EmailDomain { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }

    public int Remaining => MaxUses - UsedCount;

    public string Status(DateTimeOffset now) => RevokedAt.HasValue ? "revoked"
        : ExpiresAt <= now ? "expired" : Remaining <= 0 ? "used" : "active";

    public string? ClosedMessage(DateTimeOffset now) => Status(now) switch
        {
            "revoked" => "Администратор отозвал эту ссылку.",
            "expired" => "Срок действия приватной ссылки истёк.",
            "used" => "Лимит бронирований по этой ссылке исчерпан.",
            _ => null
        };

    public IResult? Check(DateTimeOffset now, string? email, int count = 1, PublicAppearance? appearance = null)
    {
        if (ClosedMessage(now) is { } message)
            return Results.Problem(statusCode: 410, title: message);
        if (EmailDomain is not null && !Matches(email))
            return Results.Problem(statusCode: 403, title: "Подтвердите доступ по email.",
                extensions: new Dictionary<string, object?> { ["emailDomain"] = EmailDomain, ["appearance"] = appearance });
        return count > Remaining
            ? Results.Problem(statusCode: 422, title: $"По ссылке можно создать ещё {Remaining} броней.",
                extensions: new Dictionary<string, object?> { ["remainingUses"] = Remaining })
            : null;
    }

    public bool Matches(string? email) => EmailDomain is not null && email is not null
        && email.LastIndexOf('@') is > 0 and var separator
        && TryNormalizeDomain(email[(separator + 1)..], out var domain) && domain == EmailDomain;

    public static bool TryNormalizeDomain(string? raw, out string? domain)
    {
        domain = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        try
        {
            var value = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(raw.Trim()).ToLowerInvariant();
            if (value.Length > 253 || !value.Contains('.') || value.EndsWith('.') || Uri.CheckHostName(value) != UriHostNameType.Dns)
                return false;
            domain = value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed record PrivateLinkAccess(Guid Id, string? Email);

internal static class PrivateLinkAdmin
{
    internal sealed record View(Guid Id, DateTimeOffset ExpiresAt, int MaxUses, int UsedCount, string? EmailDomain, string Status);
    private sealed record Input(DateTimeOffset ExpiresAt, int MaxUses, string? EmailDomain);

    public static void MapPrivateLinks(this RouteGroupBuilder workspace)
    {
        var links = workspace.MapGroup("/resources/{id}/private-links");
        links.MapGet("", List);
        links.MapPost("", Create);
        links.MapDelete("/{linkId:guid}", Revoke);
        links.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
    }

    private static async Task<IResult> List(string id, BookingDb db, OrganizationScope scope, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!await scope.Resources(db).AnyAsync(resource => resource.Id == id, ct))
            return Results.NotFound();
        var links = await db.PrivateBookingLinks.AsNoTracking().Where(link => link.ResourceId == id)
            .OrderByDescending(link => link.CreatedAt).ThenBy(link => link.Id).ToArrayAsync(ct);
        var now = clock.GetUtcNow();
        return Results.Ok(links.Select(link => Describe(link, now)));
    }

    private static async Task<IResult> Create(string id, Input input, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (input.MaxUses is < 1 or > 1000 || !PrivateBookingLink.TryNormalizeDomain(input.EmailDomain, out var domain))
            return Results.Problem(statusCode: 400, title: "Укажите лимит от 1 до 1000 и домен в формате example.com.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null || resource.IsArchived)
            return Results.NotFound();
        if (!await OrganizationStructure.AllowsLinks(db, id, ct))
            return OrganizationStructure.LinksDisabled();
        var now = clock.GetUtcNow();
        if (input.ExpiresAt <= now || input.ExpiresAt > now.AddDays(365))
            return Results.Problem(statusCode: 400, title: "Выберите срок действия в пределах ближайших 365 дней.");
        var link = new PrivateBookingLink
        {
            Id = Guid.NewGuid(), ResourceId = id, CreatedAt = now, ExpiresAt = input.ExpiresAt.ToUniversalTime(),
            MaxUses = input.MaxUses, EmailDomain = domain
        };
        db.PrivateBookingLinks.Add(link);
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "resource.private-link", "Создана приватная ссылка", now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(Describe(link, now), statusCode: 201);
    }

    private static async Task<IResult> Revoke(string id, Guid linkId, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Results.NotFound();
        var link = await db.PrivateBookingLinks.SingleOrDefaultAsync(item => item.Id == linkId && item.ResourceId == id, ct);
        if (link is null)
            return Results.NotFound();
        if (!link.RevokedAt.HasValue)
        {
            var now = clock.GetUtcNow();
            link.RevokedAt = now;
            Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "resource.private-link", "Отозвана приватная ссылка", now);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static View Describe(PrivateBookingLink link, DateTimeOffset now) =>
        new(link.Id, link.ExpiresAt, link.MaxUses, link.UsedCount, link.EmailDomain, link.Status(now));
}

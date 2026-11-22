using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class PrivateLinkVerification(IDataProtectionProvider protection, TimeProvider clock)
{
    internal const string MailAction = "verify-private-booking";
    private const string Cookie = "SlotGuard.PrivateAccess";
    private readonly IDataProtector proof = protection.CreateProtector("SlotGuard.PrivateLink.EmailProof.v1");
    private readonly IDataProtector access = protection.CreateProtector("SlotGuard.PrivateLink.BrowserAccess.v1");

    private sealed record Identity(Guid LinkId, string Email, DateTimeOffset ExpiresAt, Guid? Owner = null);

    public string Issue(Guid linkId, string email) => proof.Protect(JsonSerializer.Serialize(
        new Identity(linkId, email, clock.GetUtcNow().AddMinutes(15))));

    public string? ReadProof(Guid linkId, string? code) => Read(proof, linkId, code)?.Email;

    public string? ReadAccess(HttpContext context, Guid linkId, Guid? owner)
    {
        if (owner is not null && Read(access, linkId, context.Request.Cookies[Cookie]) is { } claim && claim.Owner == owner)
            return claim.Email;
        return null;
    }

    public void Enable(HttpContext context, PrivateBookingLink link, Guid owner, string email)
    {
        var now = clock.GetUtcNow();
        var until = link.ExpiresAt < now.AddDays(1) ? link.ExpiresAt : now.AddDays(1);
        var claim = new Identity(link.Id, email, until, owner);
        context.Response.Cookies.Append(Cookie, access.Protect(JsonSerializer.Serialize(claim)), new CookieOptions
        {
            Path = $"/api/public/resources/{link.Id:D}",
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            MaxAge = until - now
        });
    }

    private Identity? Read(IDataProtector protector, Guid linkId, string? token)
    {
        if (token is not { Length: > 0 and <= 3800 })
            return null;
        try
        {
            var claim = JsonSerializer.Deserialize<Identity>(protector.Unprotect(token));
            return claim?.LinkId == linkId && claim.ExpiresAt > clock.GetUtcNow() ? claim : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}

internal static class PrivateLinkVerificationEndpoints
{
    private sealed record EmailInput(string? Email);
    private sealed record ProofInput(string? Token);

    public static void MapPrivateVerification(this RouteGroupBuilder links)
    {
        links.MapPost("/verify", Send).RequireRateLimiting("private-link-email");
        links.MapPost("/verify/complete", Confirm).RequireRateLimiting("public-booking");
    }

    private static async Task<IResult> Send(Guid linkId, EmailInput input, BookingDb db, TimeProvider clock,
        PrivateLinkVerification verification, IWorkspaceMail mail, CancellationToken ct)
    {
        var link = await Find(db, linkId, ct);
        if (link?.EmailDomain is null)
            return Results.NotFound();
        if (!PublicBookingEndpoints.TryEmail(input.Email, out var email) || email is null || !link.Matches(email))
            return Results.Problem(statusCode: 422, title: $"Введите адрес в домене {link.EmailDomain}.");
        if (link.Check(clock.GetUtcNow(), email) is { } blocked)
            return blocked;
        var organization = await OrganizationStructure.Shareable(db).Where(resource => resource.Id == link.ResourceId && !resource.IsArchived)
            .Select(resource => resource.OrganizationId).SingleOrDefaultAsync(ct);
        if (organization is null)
            return Results.NotFound();
        var token = verification.Issue(linkId, email);
        var sent = await mail.Send(email, "SlotGuard · Подтвердите доступ по ссылке",
            "Откройте ссылку в течение 15 минут, чтобы подтвердить email для бронирования.",
            new MailLink(PrivateLinkVerification.MailAction, token, BookingLinkId: linkId, OrganizationId: organization), ct);
        return sent ? Results.NoContent() : Results.Problem(statusCode: 503, title: "Письмо пока не отправлено. Повторите попытку позже.");
    }

    private static async Task<IResult> Confirm(Guid linkId, ProofInput input, BookingDb db, HttpContext context,
        TimeProvider clock, PrivateLinkVerification verification, CancellationToken ct)
    {
        var link = await Find(db, linkId, ct);
        if (link?.EmailDomain is null)
            return Results.NotFound();
        if (verification.ReadProof(linkId, input.Token) is not { } email || !link.Matches(email))
            return Results.Problem(statusCode: 403, title: "Ссылка из письма недействительна или истекла.");
        if (link.Check(clock.GetUtcNow(), email) is { } blocked)
            return blocked;
        verification.Enable(context, link, await PublicBookingEndpoints.EnsureOwner(context), email);
        return Results.NoContent();
    }

    private static Task<PrivateBookingLink?> Find(BookingDb db, Guid id, CancellationToken ct) =>
        db.PrivateBookingLinks.AsNoTracking().SingleOrDefaultAsync(link => link.Id == id
            && OrganizationStructure.Shareable(db).Any(resource => resource.Id == link.ResourceId && !resource.IsArchived), ct);
}

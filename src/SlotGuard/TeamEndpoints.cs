using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using static SlotGuard.AccountEndpoints;

namespace SlotGuard;

internal sealed class OrganizationInvitation
{
    public Guid Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string Email { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; set; }
}

internal static class TeamEndpoints
{
    public static void MapTeamEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/team", GetTeam);
        api.MapPost("/team/invitations", Invite).RequireRateLimiting("account");
        api.MapPost("/team/invitations/batch", InviteBatch).RequireRateLimiting("account");
    }

    public static async Task<IResult> CreateOrganization(CreateTeam input, ClaimsPrincipal principal,
        BookingDb db, CancellationToken ct)
    {
        if (!AccountSessions.IsAccount(principal))
            return Problem(403, "Войдите в свой аккаунт, чтобы создать организацию.");
        var name = input.Name?.Trim();
        if (name is null || name.Length is 0 or > 100)
            return Problem(400, "Название должно содержать от 1 до 100 символов.");

        var organization = new Organization(Guid.NewGuid().ToString("N"), name);
        db.Organizations.Add(organization);
        db.Memberships.Add(new OrganizationMembership(DemoAccounts.OwnerId(principal), organization.Id,
            OrganizationMembership.Admin));
        await db.SaveChangesAsync(ct);
        return Results.Json(new OrganizationAccess(organization.Id, organization.Name, OrganizationMembership.Admin),
            statusCode: 201);
    }

    private static async Task<IResult> GetTeam(OrganizationScope scope, ClaimsPrincipal user, BookingDb db,
        TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin || !AccountSessions.IsAccount(user))
            return Results.Forbid();
        var members = await (
            from membership in db.Memberships
            join account in db.Users on membership.OwnerId equals account.Id
            where membership.OrganizationId == scope.Current.Id
            orderby account.DisplayName, account.Email
            select new { account.DisplayName, account.Email, membership.Role }
        ).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var invitations = await db.Invitations
            .Where(item => item.OrganizationId == scope.Current.Id && item.ExpiresAt > now && item.AcceptedAt == null)
            .OrderByDescending(item => item.ExpiresAt).Take(50)
            .Select(item => new { item.Email, item.ExpiresAt }).ToListAsync(ct);
        return Results.Ok(new { members, invitations });
    }

    private static async Task<IResult> Invite(InviteTeam input, OrganizationScope scope, ClaimsPrincipal user,
        BookingDb db, UserManager<Account> users, IWorkspaceMail mail, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin || !AccountSessions.IsAccount(user))
            return Results.Forbid();
        if (Email(input.Email) is not { } email)
            return Problem(400, "Укажите корректный email.");

        return await SendInvite(email, scope, user, db, users, mail, clock, skipPending: false, ct) switch
        {
            "sent" => Results.NoContent(),
            "member" => Problem(409, "Этот аккаунт уже состоит в организации."),
            _ => Problem(503, "Письмо не отправлено. Повторите попытку позже.")
        };
    }

    private static async Task<IResult> InviteBatch(InviteBatchInput input, OrganizationScope scope, ClaimsPrincipal user,
        BookingDb db, UserManager<Account> users, IWorkspaceMail mail, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin || !AccountSessions.IsAccount(user))
            return Results.Forbid();
        if (input.Emails is not { Length: > 0 and <= 20 })
            return Problem(400, "Укажите от 1 до 20 email.");
        var addresses = input.Emails.Select(Email).ToArray();
        if (addresses.Any(item => item is null))
            return Problem(400, "Проверьте адреса email перед отправкой.");

        var results = new List<InviteResult>();
        foreach (var address in addresses.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var status = await SendInvite(address, scope, user, db, users, mail, clock, skipPending: true, ct);
            results.Add(new InviteResult(address, status));
        }
        return Results.Ok(results);
    }

    private static async Task<string> SendInvite(string email, OrganizationScope scope, ClaimsPrincipal user, BookingDb db,
        UserManager<Account> users, IWorkspaceMail mail, TimeProvider clock, bool skipPending, CancellationToken ct)
    {
        var organization = scope.Current;
        var normalized = users.NormalizeEmail(email)!;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM "Organizations" WHERE "Id" = {organization.Id} FOR UPDATE
            """, ct);
        var account = await users.FindByEmailAsync(email);
        if (account is not null && await db.Memberships.AnyAsync(item =>
                item.OrganizationId == organization.Id && item.OwnerId == account.Id, ct))
            return "member";
        if (skipPending && await db.Invitations.AnyAsync(item => item.OrganizationId == organization.Id
            && item.Email == normalized && item.AcceptedAt == null && item.ExpiresAt > now, ct))
            return "pending";

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var invitation = new OrganizationInvitation
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, Email = normalized,
            TokenHash = Hash(token), ExpiresAt = now.AddDays(2)
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var sent = await mail.Send(email, "Приглашение в SlotGuard",
            $"Вас пригласили в организацию «{organization.Name}». Откройте ссылку и войдите с этим email. Ссылка действует 48 часов.",
            new MailLink("invite", token, OrganizationId: organization.Id), ct);
        if (!sent)
        {
            // Remove a failed invitation only if nobody opened it before SMTP reported failure.
            await db.Invitations.Where(item => item.Id == invitation.Id && item.AcceptedAt == null).ExecuteDeleteAsync(ct);
            return "failed";
        }
        Audit.Add(db, organization.Id, invitation.Id.ToString("N"), email, WorkspaceEndpoints.Actor(user),
            "structure.invited", "Приглашение отправлено по email.", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return "sent";
    }

    public static async Task<IResult> Accept(AcceptInvitation input, ClaimsPrincipal principal,
        BookingDb db, UserManager<Account> users, TimeProvider clock, CancellationToken ct)
    {
        if (!AccountSessions.IsAccount(principal))
            return Problem(403, "Войдите с email, на который пришло приглашение.");
        var account = await users.GetUserAsync(principal);
        if (account is not { EmailConfirmed: true, NormalizedEmail: { } email } || input.Token is not { Length: 43 })
            return InvalidInvitation();
        var now = clock.GetUtcNow();
        var hash = Hash(input.Token);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pending = db.Invitations.Where(item => item.TokenHash == hash && item.Email == email
            && item.AcceptedAt == null && item.ExpiresAt > now);
        var organizationId = await pending.Select(item => item.OrganizationId).SingleOrDefaultAsync(ct);
        if (organizationId is null || await pending.ExecuteUpdateAsync(set => set.SetProperty(item => item.AcceptedAt, now), ct) == 0)
            return InvalidInvitation();

        var joined = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Memberships" ("OwnerId", "OrganizationId", "Role")
            VALUES ({account.Id}, {organizationId}, {OrganizationMembership.Member})
            ON CONFLICT ("OwnerId", "OrganizationId") DO NOTHING
            """, ct);
        if (joined != 0)
        {
            Audit.Add(db, organizationId, account.Id.ToString("N"), account.DisplayName, WorkspaceEndpoints.Actor(principal),
                "structure.joined", "Вступил в организацию по приглашению.", now);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new { organizationId });
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static IResult InvalidInvitation() => Problem(400, "Приглашение недействительно для этого аккаунта.");
    internal sealed record CreateTeam(string? Name);
    internal sealed record InviteTeam(string? Email);
    private sealed record InviteBatchInput(string?[]? Emails);
    private sealed record InviteResult(string Email, string Status);
    internal sealed record AcceptInvitation(string? Token);
}

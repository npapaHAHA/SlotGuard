using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class AccountSecurity(
    BookingDb db, UserManager<Account> users, IUserClaimsPrincipalFactory<Account> principals,
    IDataProtectionProvider protection, TimeProvider clock, IConfiguration config, IOptions<IdentityOptions> identity)
{
    private const string ChallengeCookie = "SlotGuard.AccountChallenge";
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector secrets = protection.CreateProtector("SlotGuard.AuthenticatorKey.v1");
    private readonly ITimeLimitedDataProtector setup = protection.CreateProtector("SlotGuard.AuthenticatorSetup.v1").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector signIn = protection.CreateProtector("SlotGuard.SecondFactorChallenge.v1").ToTimeLimitedDataProtector();

    public static void Map(RouteGroupBuilder accounts)
    {
        accounts.MapPost("/login/factor", (HttpContext http, FactorInput input, AccountSecurity security, CancellationToken ct) =>
            security.VerifyLogin(http, input, ct));
        var settings = accounts.MapGroup("/security").RequireAuthorization();
        settings.MapGet("", (HttpContext http, AccountSecurity security, CancellationToken ct) => security.Status(http, ct));
        settings.MapPost("/setup", (HttpContext http, PasswordInput input, AccountSecurity security, CancellationToken ct) =>
            security.Setup(http, input, ct));
        settings.MapPost("/enable", (HttpContext http, EnableInput input, AccountSecurity security, CancellationToken ct) =>
            security.Enable(http, input, ct));
        settings.MapPost("/disable", (HttpContext http, ChangeInput input, AccountSecurity security, CancellationToken ct) =>
            security.Change(http, input, false, ct));
        settings.MapPost("/recovery-codes", (HttpContext http, ChangeInput input, AccountSecurity security, CancellationToken ct) =>
            security.Change(http, input, true, ct));
    }

    public async Task<IResult> Login(HttpContext http, AccountEndpoints.Credentials input, CancellationToken ct)
    {
        NoStore(http);
        if (AccountEndpoints.Email(input.Email) is not { } email || !ValidPassword(input.Password)) return BadLogin();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var matches = await db.Users.FromSqlInterpolated($"""
            SELECT * FROM "AspNetUsers" WHERE "NormalizedEmail" = {users.NormalizeEmail(email)} FOR UPDATE
            """).ToListAsync(ct);
        if (matches.SingleOrDefault() is not { EmailConfirmed: true } account || await users.IsLockedOutAsync(account))
            return BadLogin();
        if (!await users.CheckPasswordAsync(account, input.Password!))
            return await Failed(account, transaction, BadLogin(), ct);

        if (!account.TwoFactorEnabled)
        {
            account.AccessFailedCount = 0;
            if (!(await users.UpdateAsync(account)).Succeeded) return BadLogin();
            await transaction.CommitAsync(ct);
            ClearChallenge(http);
            return await SignInAccount(http, account, false);
        }

        var id = Guid.NewGuid();
        account.PendingFactorId = id;
        if (!(await users.UpdateAsync(account)).Succeeded) return BadLogin();
        await transaction.CommitAsync(ct);
        if (AccountSessions.IsAccount(http.User))
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var challenge = signIn.Protect(JsonSerializer.Serialize(new LoginChallenge(account.Id, id, account.SecurityStamp)), ChallengeLifetime);
        var cookie = Cookie(http);
        cookie.MaxAge = ChallengeLifetime;
        http.Response.Cookies.Append(ChallengeCookie, challenge, cookie);
        return Results.Json(new { requiresTwoFactor = true }, statusCode: 202);
    }

    private async Task<IResult> VerifyLogin(HttpContext http, FactorInput input, CancellationToken ct)
    {
        NoStore(http);
        var challenge = Read<LoginChallenge>(signIn, http.Request.Cookies[ChallengeCookie]);
        if (challenge is null) return ChallengeExpired();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var account = await LockAccount(challenge.Account, ct);
        if (account is not { TwoFactorEnabled: true, EmailConfirmed: true } || account.PendingFactorId != challenge.Id
            || account.SecurityStamp != challenge.Stamp || await users.IsLockedOutAsync(account))
        {
            ClearChallenge(http);
            return ChallengeExpired();
        }
        if (!UseFactor(account, input.Code, input.Recovery))
            return await Failed(account, transaction, InvalidFactor(), ct);
        account.PendingFactorId = null;
        account.AccessFailedCount = 0;
        if (!(await users.UpdateAsync(account)).Succeeded) return ChallengeExpired();
        await transaction.CommitAsync(ct);
        ClearChallenge(http);
        return await SignInAccount(http, account, true);
    }

    private async Task<IResult> Status(HttpContext http, CancellationToken ct)
    {
        NoStore(http);
        if (CurrentAccount(http) is not { } id) return Results.StatusCode(403);
        var row = await db.Users.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new { Enabled = item.TwoFactorEnabled, RecoveryCount = item.FactorRecoveryHashes.Length }).SingleOrDefaultAsync(ct);
        return row is null ? Results.Unauthorized() : Results.Ok(row);
    }

    private async Task<IResult> Setup(HttpContext http, PasswordInput input, CancellationToken ct)
    {
        NoStore(http);
        if (CurrentAccount(http) is not { } id) return Results.StatusCode(403);
        if (!ValidPassword(input.Password)) return BadPassword();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var account = await LockAccount(id, ct);
        if (!CurrentSession(http, account) || await users.IsLockedOutAsync(account!)) return BadPassword();
        if (account!.TwoFactorEnabled) return AccountEndpoints.Problem(409, "Двухфакторная защита уже включена.");
        if (!await users.CheckPasswordAsync(account, input.Password!))
            return await Failed(account, transaction, BadPassword(), ct);

        await transaction.CommitAsync(ct);
        var secret = RandomNumberGenerator.GetBytes(20);
        var key = AuthenticatorCodes.FormatKey(secret);
        var token = setup.Protect(JsonSerializer.Serialize(new Enrollment(id, account.SecurityStamp, Convert.ToHexString(secret))),
            TimeSpan.FromMinutes(10));
        var label = Uri.EscapeDataString(account.Email!);
        var uri = $"otpauth://totp/SlotGuard:{label}?secret={key}&issuer=SlotGuard&digits=6&period=30";
        return Results.Ok(new { Token = token, Key = key, Uri = uri });
    }

    private async Task<IResult> Enable(HttpContext http, EnableInput input, CancellationToken ct)
    {
        NoStore(http);
        if (CurrentAccount(http) is not { } id) return Results.StatusCode(403);
        var enrollment = Read<Enrollment>(setup, input.Token);
        if (enrollment is null || enrollment.Account != id)
            return AccountEndpoints.Problem(400, "Настройка истекла. Запросите новый ключ.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var account = await LockAccount(id, ct);
        if (!CurrentSession(http, account) || account!.TwoFactorEnabled || account.SecurityStamp != enrollment.Stamp
            || await users.IsLockedOutAsync(account))
            return AccountEndpoints.Problem(409, "Начните настройку заново после входа.");
        if (AuthenticatorCodes.Match(Convert.FromHexString(enrollment.Secret), input.Code, clock.GetUtcNow()) is not { } step)
            return await Failed(account, transaction, InvalidFactor(), ct);

        account.ProtectedAuthenticatorKey = secrets.Protect(enrollment.Secret);
        account.LastAuthenticatorStep = step;
        account.TwoFactorEnabled = true;
        var recovery = ReplaceRecoveryCodes(account);
        return await FinishChange(http, account, transaction, recovery, ct);
    }

    private async Task<IResult> Change(HttpContext http, ChangeInput input, bool rotate, CancellationToken ct)
    {
        NoStore(http);
        if (CurrentAccount(http) is not { } id) return Results.StatusCode(403);
        if (!ValidPassword(input.Password)) return BadPassword();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var account = await LockAccount(id, ct);
        if (!CurrentSession(http, account) || account is not { TwoFactorEnabled: true } || await users.IsLockedOutAsync(account))
            return BadPassword();
        if (!await users.CheckPasswordAsync(account, input.Password!))
            return await Failed(account, transaction, BadPassword(), ct);
        if (!UseFactor(account, input.Code, input.Recovery))
            return await Failed(account, transaction, InvalidFactor(), ct);

        if (rotate) return await FinishChange(http, account, transaction, ReplaceRecoveryCodes(account), ct);
        account.TwoFactorEnabled = false;
        account.ProtectedAuthenticatorKey = null;
        account.LastAuthenticatorStep = null;
        account.FactorRecoveryHashes = [];
        return await FinishChange(http, account, transaction, [], ct);
    }

    private async Task<IResult> FinishChange(HttpContext http, Account account, IDbContextTransaction transaction,
        string[] recovery, CancellationToken ct)
    {
        account.PendingFactorId = null;
        account.AccessFailedCount = 0;
        if (!(await users.UpdateSecurityStampAsync(account)).Succeeded)
            return AccountEndpoints.Problem(409, "Настройки изменились. Повторите действие.");
        await transaction.CommitAsync(ct);
        ClearChallenge(http);
        await SignInAccount(http, account, account.TwoFactorEnabled);
        return Results.Ok(new { Enabled = account.TwoFactorEnabled, RecoveryCodes = recovery });
    }

    private bool UseFactor(Account account, string? code, bool recovery)
    {
        if (recovery)
        {
            if (code is not { Length: > 0 and <= 40 }) return false;
            var normalized = code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
            if (normalized.Length != 24 || !normalized.All(char.IsAsciiHexDigit)) return false;
            var hash = WebhookEvents.HashKey(normalized);
            if (!account.FactorRecoveryHashes.Contains(hash, StringComparer.Ordinal)) return false;
            account.FactorRecoveryHashes = account.FactorRecoveryHashes.Where(item => item != hash).ToArray();
            return true;
        }

        if (account.ProtectedAuthenticatorKey is null) return false;
        try
        {
            var key = Convert.FromHexString(secrets.Unprotect(account.ProtectedAuthenticatorKey));
            if (AuthenticatorCodes.Match(key, code, clock.GetUtcNow(), account.LastAuthenticatorStep) is not { } step)
                return false;
            account.LastAuthenticatorStep = step;
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    private static string[] ReplaceRecoveryCodes(Account account)
    {
        var values = Enumerable.Range(0, 8).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(12))).ToArray();
        account.FactorRecoveryHashes = values.Select(WebhookEvents.HashKey).ToArray();
        return values.Select(value => $"{value[..6]}-{value[6..12]}-{value[12..18]}-{value[18..]}").ToArray();
    }

    private async Task<Account?> LockAccount(Guid id, CancellationToken ct) =>
        (await db.Users.FromSqlInterpolated($"""SELECT * FROM "AspNetUsers" WHERE "Id" = {id} FOR UPDATE""")
            .ToListAsync(ct)).SingleOrDefault();

    private async Task<IResult> Failed(Account account, IDbContextTransaction transaction, IResult result, CancellationToken ct)
    {
        if ((await users.AccessFailedAsync(account)).Succeeded) await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<IResult> SignInAccount(HttpContext http, Account account, bool factor)
    {
        var principal = await principals.CreateAsync(account);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("amr", factor ? "mfa" : "pwd"));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return AccountSessions.Describe(account.DisplayName, !string.IsNullOrEmpty(config["DemoAccounts:Password"]));
    }

    private bool CurrentSession(HttpContext http, Account? account) =>
        account is not null && account.SecurityStamp == http.User.FindFirstValue(identity.Value.ClaimsIdentity.SecurityStampClaimType);

    private static Guid? CurrentAccount(HttpContext http) => AccountSessions.IsAccount(http.User)
        && Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static T? Read<T>(ITimeLimitedDataProtector protector, string? value) where T : class
    {
        if (value is not { Length: > 0 and <= 2048 }) return null;
        try { return JsonSerializer.Deserialize<T>(protector.Unprotect(value)); }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static void ClearChallenge(HttpContext http) => http.Response.Cookies.Delete(ChallengeCookie, Cookie(http));
    private static CookieOptions Cookie(HttpContext http) => new()
    {
        Path = "/api/account/login", HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, IsEssential = true
    };
    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
    private static bool ValidPassword(string? password) => password is { Length: >= 10 and <= 128 };
    private static IResult BadLogin() => AccountEndpoints.Problem(401, "Проверьте email, пароль и подтверждение почты.");
    private static IResult BadPassword() => AccountEndpoints.Problem(401, "Проверьте пароль и состояние аккаунта.");
    private static IResult InvalidFactor() => AccountEndpoints.Problem(401, "Введите новый код из приложения или действующий код восстановления.");
    private static IResult ChallengeExpired() => AccountEndpoints.Problem(401, "Сеанс подтверждения истёк. Войдите с паролем заново.");

    private sealed record LoginChallenge(Guid Account, Guid Id, string? Stamp);
    private sealed record Enrollment(Guid Account, string? Stamp, string Secret);
    private sealed record PasswordInput(string? Password);
    private sealed record EnableInput(string? Token, string? Code);
    private sealed record FactorInput(string? Code, bool Recovery = false);
    private sealed record ChangeInput(string? Password, string? Code, bool Recovery = false);
}

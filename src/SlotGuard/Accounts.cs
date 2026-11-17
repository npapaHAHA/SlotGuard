using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SlotGuard;

public sealed class Account : IdentityUser<Guid>
{
    [MaxLength(100)]
    public required string DisplayName { get; init; }
}

internal sealed class AccountPrincipalFactory(UserManager<Account> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<Account>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Account user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));
        return identity;
    }
}

internal static class AccountSessions
{
    public static bool IsAccount(ClaimsPrincipal? user) => user?.HasClaim(claim => claim.Type == ClaimTypes.GivenName) == true;

    public static IResult Describe(string name, bool demoEnabled) =>
        Results.Ok(new { label = name, demoEnabled, isDemo = false, isAccount = true });
}

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var accounts = app.MapGroup("/api/account").RequireRateLimiting("account");
        accounts.MapPost("/register", Register);
        accounts.MapPost("/login", Login);
        accounts.MapPost("/confirm", ConfirmEmail);
        accounts.MapPost("/email", RequestEmail);
        accounts.MapPost("/reset", ResetPassword);
        accounts.MapPost("/organizations", TeamEndpoints.CreateOrganization).RequireAuthorization();
        accounts.MapPost("/invitations/accept", TeamEndpoints.Accept).RequireAuthorization();
    }

    private static async Task<IResult> Register(Registration input, UserManager<Account> users, IWorkspaceMail mail,
        CancellationToken ct)
    {
        var name = input.Name?.Trim();
        var email = Email(input.Email);
        if (name is null || name.Length is 0 or > 100 || email is null || !ValidPasswordLength(input.Password))
            return Problem(400, "Укажите имя, email и пароль от 10 до 128 символов.");

        var account = new Account
        {
            Id = Guid.NewGuid(), DisplayName = name, Email = email, UserName = email
        };
        if (await users.FindByEmailAsync(email) is not null)
            return Results.NoContent();
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(account, input.Password!);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
        {
            return Results.NoContent();
        }
        if (!result.Succeeded)
            return result.Errors.Any(error => error.Code is "DuplicateUserName" or "DuplicateEmail")
                ? Results.NoContent()
                : PasswordProblem();

        return await SendConfirmation(account, users, mail, ct);
    }

    private static async Task<IResult> Login(Credentials input, UserManager<Account> users, SignInManager<Account> signIn,
        HttpContext context, IConfiguration config)
    {
        if (Email(input.Email) is not { } email || !ValidPasswordLength(input.Password))
            return BadLogin();
        var account = await users.FindByEmailAsync(email);
        if (account is null)
            return BadLogin();
        var result = await signIn.CheckPasswordSignInAsync(account, input.Password!, lockoutOnFailure: true);
        if (!result.Succeeded)
            return BadLogin();

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            await signIn.CreateUserPrincipalAsync(account));
        return AccountSessions.Describe(account.DisplayName, !string.IsNullOrEmpty(config["DemoAccounts:Password"]));
    }

    private static async Task<IResult> ConfirmEmail(EmailToken input, UserManager<Account> users)
    {
        var account = await FindTokenAccount(input.UserId, input.Token, users);
        if (account is null || !(await users.ConfirmEmailAsync(account, input.Token!)).Succeeded)
            return InvalidLink();
        return Results.NoContent();
    }

    private static async Task<IResult> RequestEmail(EmailRequest input, UserManager<Account> users,
        IWorkspaceMail mail, CancellationToken ct)
    {
        var email = Email(input.Email);
        if (email is null || input.Purpose is not ("confirm" or "reset"))
            return Problem(400, "Укажите корректный email.");
        var account = await users.FindByEmailAsync(email);
        if (account is null)
            return Results.NoContent();

        if (input.Purpose == "confirm")
            return account.EmailConfirmed ? Results.NoContent() : await SendConfirmation(account, users, mail, ct);
        if (!account.EmailConfirmed)
            return Results.NoContent();
        var token = await users.GeneratePasswordResetTokenAsync(account);
        return await Send(mail, email, "Сброс пароля SlotGuard", "Откройте ссылку, чтобы задать новый пароль.",
            new MailLink("reset", token, account.Id), ct);
    }

    private static async Task<IResult> ResetPassword(PasswordReset input, UserManager<Account> users)
    {
        if (!ValidPasswordLength(input.Password))
            return PasswordProblem();
        var account = await FindTokenAccount(input.UserId, input.Token, users);
        if (account is null || !account.EmailConfirmed)
            return InvalidLink();
        var result = await users.ResetPasswordAsync(account, input.Token!, input.Password!);
        if (result.Succeeded)
            return Results.NoContent();
        return result.Errors.Any(error => error.Code == "InvalidToken") ? InvalidLink() : PasswordProblem();
    }

    private static async Task<Account?> FindTokenAccount(Guid userId, string? token, UserManager<Account> users) =>
        token is { Length: > 0 and <= 2048 } ? await users.FindByIdAsync(userId.ToString()) : null;

    private static async Task<IResult> SendConfirmation(Account account, UserManager<Account> users,
        IWorkspaceMail mail, CancellationToken ct)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(account);
        return await Send(mail, account.Email!, "Подтвердите email в SlotGuard", "Откройте ссылку, чтобы подтвердить email.",
            new MailLink("confirm", token, account.Id), ct);
    }

    internal static async Task<IResult> Send(IWorkspaceMail mail, string email, string subject, string message,
        MailLink link, CancellationToken ct) =>
        await mail.Send(email, subject, message, link, ct)
            ? Results.NoContent()
            : Problem(503, "Почта временно недоступна. Повторите отправку письма позже.");

    internal static string? Email(string? candidate)
    {
        var email = candidate?.Trim();
        return email is { Length: > 0 and <= 254 } && new EmailAddressAttribute().IsValid(email) ? email : null;
    }

    private static bool ValidPasswordLength(string? password) => password is { Length: >= 10 and <= 128 };
    private static IResult BadLogin() => Problem(401, "Проверьте email, пароль и подтверждение почты.");
    private static IResult InvalidLink() => Problem(400, "Ссылка недействительна или её срок истёк.");
    private static IResult PasswordProblem() => Problem(400,
        "Пароль должен содержать от 10 до 128 символов, строчную и заглавную буквы, цифру и спецсимвол.");
    internal static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);

    internal sealed record Registration(string? Name, string? Email, string? Password);
    internal sealed record Credentials(string? Email, string? Password);
    internal sealed record EmailToken(Guid UserId, string? Token);
    internal sealed record EmailRequest(string? Email, string? Purpose);
    internal sealed record PasswordReset(Guid UserId, string? Token, string? Password);
}

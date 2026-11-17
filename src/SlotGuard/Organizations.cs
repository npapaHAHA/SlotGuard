using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record OrganizationAccess(string Id, string Name, string Role);

internal sealed class OrganizationScope
{
    public const string HeaderName = "X-SlotGuard-Organization";
    public OrganizationAccess Current { get; set; } = null!;

    public bool IsAdmin => Current.Role == OrganizationMembership.Admin;
    public IQueryable<Resource> Resources(BookingDb db) =>
        db.Resources.Where(resource => resource.OrganizationId == Current.Id);

    public static async Task<List<OrganizationAccess>> ForUser(BookingDb db, ClaimsPrincipal user, CancellationToken ct)
    {
        var owner = DemoAccounts.OwnerId(user);
        var access = await (
            from membership in db.Memberships
            join organization in db.Organizations on membership.OrganizationId equals organization.Id
            where membership.OwnerId == owner
            orderby organization.Id != Organization.University, organization.Name
            select new OrganizationAccess(organization.Id, organization.Name, membership.Role)
        ).ToListAsync(ct);
        if (access.Count != 0 || user.Identity?.Name is not null)
            return access;

        // Anonymous demo guests, including existing signed sessions, belong to the university.
        var university = await db.Organizations.SingleAsync(item => item.Id == Organization.University, ct);
        return [new OrganizationAccess(university.Id, university.Name, OrganizationMembership.Member)];
    }
}

internal sealed class OrganizationFilter(BookingDb db, OrganizationScope scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var id = http.Request.Headers.TryGetValue(OrganizationScope.HeaderName, out var header)
            ? header.ToString()
            : Organization.University;
        var access = await OrganizationScope.ForUser(db, http.User, http.RequestAborted);
        var current = access.Find(item => item.Id == id);
        if (current is null)
            return Results.NotFound();
        scope.Current = current;
        return await next(context);
    }
}

internal static class DemoAccounts
{
    public static readonly Guid UniversityAdmin = Guid.Parse("9e4f30ad-030d-4d28-929a-f028f82f4421");
    public static readonly Guid CoworkingAdmin = Guid.Parse("1e72ddf8-da00-45fe-9fe6-63ca75a7919a");
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static Guid OwnerId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static string LabelFor(Guid id) => id == UniversityAdmin ? "Администратор университета"
        : id == CoworkingAdmin ? "Администратор коворкинга"
        : $"Гость {id.ToString("N")[..4].ToUpperInvariant()}";

    public static async Task<IResult> Session(HttpContext context, IConfiguration config)
    {
        var enabled = !string.IsNullOrEmpty(config["DemoAccounts:Password"]);
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return await StartGuest(context, config);

        return AccountSessions.IsAccount(context.User)
            ? AccountSessions.Describe(context.User.FindFirstValue(ClaimTypes.GivenName)!, enabled)
            : Describe(id, context.User.Identity?.Name, enabled);
    }

    public static async Task<IResult> StartGuest(HttpContext context, IConfiguration config)
    {
        var id = Guid.NewGuid();
        await SignIn(context, id);
        return Describe(id, null, !string.IsNullOrEmpty(config["DemoAccounts:Password"]));
    }

    public static async Task<IResult> SignInDemo(DemoLogin request, HttpContext context, IConfiguration config)
    {
        var expected = config["DemoAccounts:Password"];
        if (string.IsNullOrEmpty(expected))
            return Results.NotFound();

        var profile = request.Login switch
        {
            "university-admin" => UniversityAdmin,
            "coworking-admin" => CoworkingAdmin,
            _ => Guid.Empty
        };
        if (profile == Guid.Empty || string.IsNullOrEmpty(request.Password) || request.Password.Length > 256
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(request.Password))))
        {
            return Results.Problem(statusCode: 401, title: "Неверный демо-аккаунт или пароль.");
        }

        var label = LabelFor(profile);
        await SignIn(context, profile, label);
        return Describe(profile, label, true);
    }

    private static Task SignIn(HttpContext context, Guid id, string? label = null)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme);
        if (label is not null)
            identity.AddClaim(new Claim(ClaimTypes.Name, label));
        return context.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    private static IResult Describe(Guid id, string? label, bool enabled) =>
        Results.Ok(new { label = label ?? LabelFor(id), demoEnabled = enabled,
            isDemo = label is not null, isAccount = false });

    internal sealed record DemoLogin(string Login, string Password);
}

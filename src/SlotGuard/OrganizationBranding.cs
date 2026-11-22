using System.Net.Mail;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class OrganizationBranding
{
    public required string OrganizationId { get; init; }
    public string? Name { get; set; }
    public string? LogoUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string Palette { get; set; } = "forest";
    public string? CompletionUrl { get; set; }
    public string? CompletionLabel { get; set; }
    public string? SenderName { get; set; }
    public string? ReplyTo { get; set; }
    public string? SmtpProfile { get; set; }

    public PublicAppearance Public() => new(Name, LogoUrl, BannerUrl, Palette, CompletionUrl, CompletionLabel);
}

internal sealed record PublicAppearance(string? Name, string? LogoUrl, string? BannerUrl, string Palette,
    string? CompletionUrl, string? CompletionLabel);

internal static class OrganizationBrandingEndpoints
{
    public static void MapOrganizationBranding(this RouteGroupBuilder api)
    {
        var branding = api.MapGroup("/workspace/branding").AddEndpointFilter((context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<OrganizationScope>().IsAdmin
                ? next(context) : ValueTask.FromResult<object?>(Results.StatusCode(403)));
        branding.MapGet("", Read);
        branding.MapPut("", Save);
    }

    private static async Task<IResult> Read(BookingDb db, OrganizationScope scope, IOptions<WorkspaceMailOptions> mail,
        HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var organization = scope.Current.Id;
        var settings = await db.Branding.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organization, ct);
        var profiles = mail.Value.Profiles.Where(item => item.Key.Length <= 64 && item.Value.IsConfigured)
            .OrderBy(item => item.Key).Select(item => new { id = item.Key, name = item.Value.Label ?? item.Key });
        return Results.Ok(new { settings = settings ?? new OrganizationBranding { OrganizationId = organization }, profiles });
    }

    private static async Task<IResult> Save(Input input, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        IOptions<WorkspaceMailOptions> mail, TimeProvider clock, CancellationToken ct)
    {
        if (!ReadText(input.Name, 80, out var name) || !ReadText(input.SenderName, 80, out var sender)
            || !ReadText(input.CompletionLabel, 60, out var label))
            return Invalid("Проверьте название, имя отправителя и подпись кнопки.");
        if (!ReadUrl(input.LogoUrl, out var logo) || !ReadUrl(input.BannerUrl, out var banner)
            || !ReadUrl(input.CompletionUrl, out var completion))
            return Invalid("Введите корректные HTTPS-адреса без имени пользователя и пароля.");
        if ((completion is null) != (label is null))
            return Invalid("Укажите адрес перехода и подпись кнопки вместе.");
        if (input.Palette is not ("forest" or "ocean" or "plum" or "graphite"))
            return Invalid("Выберите оформление из списка.");
        var replyTo = string.IsNullOrWhiteSpace(input.ReplyTo) ? null : input.ReplyTo.Trim();
        if (replyTo is not null && (replyTo.Length > 254 || replyTo.Any(char.IsControl)
                || !MailAddress.TryCreate(replyTo, out var address) || address.Address != replyTo))
            return Invalid("Введите один email для ответов.");
        var profile = string.IsNullOrEmpty(input.SmtpProfile) ? null : input.SmtpProfile;
        if (profile is not null && (profile.Length > 64
            || !mail.Value.Profiles.TryGetValue(profile, out var transport) || !transport.IsConfigured))
            return Invalid("Выберите доступный почтовый профиль.");

        var organization = scope.Current.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM "Organizations" WHERE "Id" = {organization} FOR UPDATE
            """, ct);
        var previous = await db.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organization, ct);
        var settings = previous ?? new OrganizationBranding { OrganizationId = organization };
        settings.Name = name;
        settings.LogoUrl = logo;
        settings.BannerUrl = banner;
        settings.Palette = input.Palette;
        settings.CompletionUrl = completion;
        settings.CompletionLabel = label;
        settings.SenderName = sender;
        settings.ReplyTo = replyTo;
        settings.SmtpProfile = profile;
        if (previous is null) db.Branding.Add(settings);
        db.ChangeTracker.DetectChanges();
        if (db.Entry(settings).State != EntityState.Unchanged)
        {
            Audit.Add(db, organization, organization, scope.Current.Name, WorkspaceEndpoints.Actor(user),
                "organization.branding", "Обновлены оформление и настройки писем.", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static bool ReadText(string? input, int maximum, out string? text)
    {
        text = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        return text is null || text.Length <= maximum && !text.Any(char.IsControl);
    }

    private static bool ReadUrl(string? input, out string? url)
    {
        url = null;
        if (!ReadText(input, 512, out var text)) return false;
        if (text is null) return true;
        if (text.Contains('\\') || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 512)
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

    private static IResult Invalid(string title) => Results.Problem(statusCode: 400, title: title);

    private sealed record Input(string? Name, string? LogoUrl, string? BannerUrl, string? Palette,
        string? CompletionUrl, string? CompletionLabel, string? SenderName, string? ReplyTo, string? SmtpProfile);
}

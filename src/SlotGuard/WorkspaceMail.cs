using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal class WorkspaceSmtpProfile
{
    public string? Label { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "slotguard@localhost";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535
        && MailAddress.TryCreate(From, out _);
}

internal sealed class WorkspaceMailOptions : WorkspaceSmtpProfile
{
    public string PublicUrl { get; set; } = "http://127.0.0.1:8080/";
    public Dictionary<string, WorkspaceSmtpProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record MailLink(string Action, string Token, Guid? UserId = null, Guid? BookingLinkId = null,
    string? OrganizationId = null, Guid? EventId = null);

internal interface IWorkspaceMail
{
    Task<bool> Send(string email, string subject, string message, MailLink link, CancellationToken ct);
}

internal sealed class SmtpWorkspaceMail(IOptions<WorkspaceMailOptions> options, BookingDb db, ILogger<SmtpWorkspaceMail> logger)
    : IWorkspaceMail, IBookingMail
{
    public Task<bool> Send(string email, string subject, string message, MailLink link, CancellationToken ct)
    {
        if (link.EventId is { } eventId)
            return Deliver(email, subject, message, url => new Uri(url, $"/events/{eventId:D}").AbsoluteUri,
                null, ct, "Открыть мероприятие", link.OrganizationId);
        if (link.Action == PrivateLinkVerification.MailAction && link.BookingLinkId is { } linkId)
            return Deliver(email, subject, message, url => new Uri(url, $"/book/{linkId:D}").AbsoluteUri
                + "#verify=" + Uri.EscapeDataString(link.Token), null, ct, "Подтвердить email", link.OrganizationId);
        return Deliver(email, subject, message, url => QueryHelpers.AddQueryString(url.AbsoluteUri, new Dictionary<string, string?>
        {
            ["action"] = link.Action, ["token"] = link.Token, ["userId"] = link.UserId?.ToString()
        }), null, ct, organizationId: link.OrganizationId);
    }

    public Task<bool> SendConfirmation(BookingConfirmation confirmation, CancellationToken ct)
    {
        var message = $"""
            Ваша бронь подтверждена.

            Ресурс: {confirmation.Resource}
            Где: {confirmation.Location}
            Когда: {confirmation.Time}

            Добавьте бронь в календарь с помощью файла slotguard.ics.
            {(confirmation.ManagementToken is null ? "Откройте «Мои записи», чтобы изменить бронь." :
                "По личной ссылке можно перенести или отменить бронь. Не пересылайте её другим.")}
            """;
        return Deliver(confirmation.Email, "SlotGuard · Бронь подтверждена", message,
            url => BookingLink(url, confirmation.ManagementToken),
            confirmation.Calendar, ct, "Открыть бронь в SlotGuard", confirmation.OrganizationId);
    }

    public Task<bool> SendReminder(BookingReminder reminder, CancellationToken ct)
    {
        var subject = reminder.Kind == BookingEmailKind.DayBefore
            ? "SlotGuard · Напоминание о брони за 24 часа"
            : "SlotGuard · Напоминание о брони за 1 час";
        var message = $"""
            Напоминаем о вашей предстоящей брони.

            Ресурс: {reminder.Resource}
            Где: {reminder.Location}
            Когда: {reminder.Time}
            """;
        return Deliver(reminder.Email, subject, message, url => BookingLink(url, reminder.ManagementToken),
            null, ct, "Открыть бронь в SlotGuard", reminder.OrganizationId);
    }

    private static string BookingLink(Uri url, string? token) => token is not null
        ? new Uri(url, "/manage-booking").AbsoluteUri + "#" + Uri.EscapeDataString(token)
        : new UriBuilder(url) { Fragment = "mine" }.Uri.AbsoluteUri;

    private async Task<bool> Deliver(string email, string subject, string message, Func<Uri, string> link,
        byte[]? calendar, CancellationToken ct, string? actionLabel = null, string? organizationId = null)
    {
        var branding = organizationId is null ? null : await db.Branding.AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.OrganizationId == organizationId, ct);
        var app = options.Value;
        var config = branding?.SmtpProfile is { } profile ? app.Profiles.GetValueOrDefault(profile) : app;
        if (config is not { IsConfigured: true }
            || !MailAddress.TryCreate(config.From, out var sender)
            || !Uri.TryCreate(app.PublicUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(url.Fragment) || !MailAddress.TryCreate(email, out var recipient))
        {
            logger.LogWarning("SlotGuard email is unavailable. Check SMTP and public URL settings.");
            return false;
        }

        var href = link(url);
        if (branding?.SenderName is { } name)
            sender = new MailAddress(sender.Address, name, Encoding.UTF8);
        using var mail = new MailMessage(sender, recipient)
        {
            Subject = subject, Body = $"{message}\n\n{href}"
        };
        if (branding?.ReplyTo is { } replyTo)
            mail.ReplyToList.Add(new MailAddress(replyTo));
        if (calendar is not null)
            mail.Attachments.Add(new Attachment(new MemoryStream(calendar), BookingCalendar.FileName, BookingCalendar.ContentType));
        if (actionLabel is not null)
        {
            var content = WebUtility.HtmlEncode(message).ReplaceLineEndings("<br>");
            var html = $"""
                <!doctype html>
                <html lang="ru"><head><meta charset="utf-8"></head>
                <body><p>{content}</p><p><a href="{WebUtility.HtmlEncode(href)}">{WebUtility.HtmlEncode(actionLabel)}</a></p></body></html>
                """;
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));
        }
        using var client = new SmtpClient(config.Host!, config.Port) { EnableSsl = config.StartTls };
        if (!string.IsNullOrEmpty(config.User))
            client.Credentials = new NetworkCredential(config.User, config.Password);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.SendMailAsync(mail, timeout.Token);
            return true;
        }
        catch (Exception error) when (error is SmtpException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("SMTP did not confirm delivery of SlotGuard mail ({ErrorType}).", error.GetType().Name);
            return false;
        }
    }
}

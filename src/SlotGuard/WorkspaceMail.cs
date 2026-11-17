using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class WorkspaceMailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "slotguard@localhost";
    public string PublicUrl { get; set; } = "http://127.0.0.1:8080/";
}

internal sealed record MailLink(string Action, string Token, Guid? UserId = null);

internal interface IWorkspaceMail
{
    Task<bool> Send(string email, string subject, string message, MailLink link, CancellationToken ct);
}

internal sealed class SmtpWorkspaceMail(IOptions<WorkspaceMailOptions> options, ILogger<SmtpWorkspaceMail> logger)
    : IWorkspaceMail
{
    public async Task<bool> Send(string email, string subject, string message, MailLink link, CancellationToken ct)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.Host) || config.Port is < 1 or > 65535
            || !MailAddress.TryCreate(config.From, out var sender)
            || !Uri.TryCreate(config.PublicUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(url.Fragment) || !MailAddress.TryCreate(email, out var recipient))
        {
            logger.LogWarning("SlotGuard email is unavailable. Check SMTP and public URL settings.");
            return false;
        }

        var href = QueryHelpers.AddQueryString(url.AbsoluteUri, new Dictionary<string, string?>
        {
            ["action"] = link.Action, ["token"] = link.Token, ["userId"] = link.UserId?.ToString()
        });
        using var mail = new MailMessage(sender, recipient) { Subject = subject, Body = $"{message}\n\n{href}" };
        using var client = new SmtpClient(config.Host, config.Port) { EnableSsl = config.StartTls };
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

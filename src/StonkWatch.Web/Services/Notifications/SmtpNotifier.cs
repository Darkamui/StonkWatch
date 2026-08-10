using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace StonkWatch.Web.Services.Notifications;

/// <summary>
/// Sends via SMTP using MailKit. <c>System.Net.Mail.SmtpClient</c> is obsolete and does not
/// handle modern TLS negotiation, so it is not an option here.
/// </summary>
public class SmtpNotifier(
    IOptions<SmtpOptions> options, ILogger<SmtpNotifier> logger) : INotifier
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(_options.From));
        mime.To.Add(MailboxAddress.Parse(_options.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            TextBody = message.PlainTextBody,
            HtmlBody = message.HtmlBody
        }.ToMessageBody();

        using var client = new SmtpClient();

        var socketOptions = _options.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        };

        await client.ConnectAsync(_options.Host, _options.Port, socketOptions, ct);

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password ?? "", ct);
        }

        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);

        logger.LogInformation("Sent notification: {Subject}", message.Subject);
    }
}

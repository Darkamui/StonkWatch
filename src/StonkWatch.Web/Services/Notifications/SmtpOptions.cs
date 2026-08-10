using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.Notifications;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>
    /// How to secure the connection. <see cref="SmtpSecurity.Auto"/> suits both common cases —
    /// STARTTLS on port 587 and implicit TLS on 465 — so it is rarely worth setting.
    /// </summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Envelope sender. Gmail rewrites this to the authenticated account.</summary>
    [Required]
    [EmailAddress]
    public string From { get; set; } = "";

    [Required]
    [EmailAddress]
    public string To { get; set; } = "";
}

public enum SmtpSecurity
{
    /// <summary>Let MailKit choose from the port and the server's capabilities.</summary>
    Auto,

    /// <summary>Connect in the clear, then upgrade. The usual choice for port 587.</summary>
    StartTls,

    /// <summary>TLS from the first byte. Port 465.</summary>
    SslOnConnect,

    /// <summary>No encryption. Only for a local mail sink such as Mailpit.</summary>
    None
}

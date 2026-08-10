namespace StonkWatch.Web.Services.Notifications;

public record NotificationMessage(string Subject, string PlainTextBody, string HtmlBody);

public interface INotifier
{
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}

using System.Globalization;
using System.Net;
using System.Text;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.Monitoring;

namespace StonkWatch.Web.Services.Notifications;

public record AlertNotification(string Ticker, string? Company, LevelCrossing Crossing);

/// <summary>
/// Renders one check cycle's crossings into a single email. Pure so the wording and escaping
/// can be tested without an SMTP server.
/// </summary>
public static class AlertDigest
{
    private const int MaxTickersInSubject = 3;

    public static NotificationMessage Build(
        IReadOnlyList<AlertNotification> alerts, string? publicBaseUrl = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(alerts.Count);

        var byTicker = alerts
            .GroupBy(a => a.Ticker, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        return new NotificationMessage(
            BuildSubject(alerts, byTicker.Select(g => g.Key).ToList()),
            BuildPlainText(byTicker, publicBaseUrl),
            BuildHtml(byTicker, publicBaseUrl));
    }

    private static string BuildSubject(
        IReadOnlyList<AlertNotification> alerts, IReadOnlyList<string> tickers)
    {
        if (alerts.Count == 1)
        {
            var only = alerts[0];
            return $"StonkWatch: {only.Ticker} {Describe(only.Crossing)}";
        }

        var shown = string.Join(", ", tickers.Take(MaxTickersInSubject));
        if (tickers.Count > MaxTickersInSubject)
        {
            shown += $" +{tickers.Count - MaxTickersInSubject} more";
        }

        return $"StonkWatch: {alerts.Count} alerts — {shown}";
    }

    private static string BuildPlainText(
        List<IGrouping<string, AlertNotification>> byTicker, string? baseUrl)
    {
        var sb = new StringBuilder();

        foreach (var group in byTicker)
        {
            var company = group.First().Company;
            sb.Append(group.Key);
            if (!string.IsNullOrWhiteSpace(company))
            {
                sb.Append(" — ").Append(company);
            }
            sb.AppendLine();

            foreach (var alert in Ordered(group))
            {
                sb.Append("  · ").AppendLine(Describe(alert.Crossing));
            }

            if (DetailUrl(baseUrl, group.Key) is { } url)
            {
                sb.Append("  ").AppendLine(url);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string BuildHtml(
        List<IGrouping<string, AlertNotification>> byTicker, string? baseUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:15px\">");

        foreach (var group in byTicker)
        {
            var company = group.First().Company;
            var url = DetailUrl(baseUrl, group.Key);
            var heading = Encode(group.Key);

            sb.Append("<p style=\"margin:0 0 4px\"><strong>");
            sb.Append(url is null ? heading : $"<a href=\"{Encode(url)}\">{heading}</a>");
            sb.Append("</strong>");

            if (!string.IsNullOrWhiteSpace(company))
            {
                sb.Append(" <span style=\"color:#666\">").Append(Encode(company)).Append("</span>");
            }

            sb.Append("</p><ul style=\"margin:0 0 16px;padding-left:20px\">");

            foreach (var alert in Ordered(group))
            {
                sb.Append("<li>").Append(Encode(Describe(alert.Crossing))).Append("</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Worst news first within a ticker, matching the evaluator's level order.</summary>
    private static IEnumerable<AlertNotification> Ordered(IEnumerable<AlertNotification> alerts) =>
        alerts.OrderBy(a => a.Crossing.Level.Type switch
        {
            AlertType.Invalidation => 0,
            AlertType.PrimarySupport => 1,
            AlertType.SecondarySupport => 2,
            AlertType.ReclaimTrigger => 3,
            AlertType.Target => 4,
            _ => 5
        }).ThenBy(a => a.Crossing.Level.Value);

    public static string Describe(LevelCrossing crossing)
    {
        var level = crossing.Level;
        var verb = level.Type switch
        {
            AlertType.Invalidation => "broke",
            AlertType.PrimarySupport or AlertType.SecondarySupport => "entered",
            AlertType.ReclaimTrigger => "reclaimed",
            AlertType.Target => "hit",
            _ => "crossed"
        };

        return $"{verb} {level.Label} {Money(level.Value)} — now {Money(crossing.Price)}";
    }

    /// <summary>
    /// Always invariant: the host locale may use a comma as the decimal separator, which
    /// would render "42,00" in an email about a dollar price.
    /// </summary>
    private static string Money(decimal value) =>
        value.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Must match the Razor route for Pages/Candidates/Detail.cshtml (<c>@page "{ticker}"</c>),
    /// which is /Candidates/Detail/{ticker} — not /Candidates/{ticker}.
    /// </summary>
    private static string? DetailUrl(string? baseUrl, string ticker) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : $"{baseUrl.TrimEnd('/')}/Candidates/Detail/{Uri.EscapeDataString(ticker)}";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

using System.Globalization;
using System.Text.Json;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>One entry from Questrade's <c>v1/markets/quotes</c> batch response.</summary>
public record QuestradeQuote(
    int SymbolId, string Symbol, decimal? LastTradePrice,
    decimal? LastTradePriceTrHrs, long? Volume);

/// <summary>
/// One candle from Questrade's <c>v1/markets/candles</c> response. <see cref="Start"/> is the
/// beginning of the period the candle covers — for a daily candle, midnight Eastern on the day
/// it belongs to — which is what lets a caller tell one session's close from the next.
/// </summary>
public record QuestradeCandle(DateTimeOffset Start, decimal Close);

public interface IQuestradeQuoteClient
{
    /// <summary>One request for the whole list — never one call per symbol.</summary>
    Task<IReadOnlyDictionary<int, QuestradeQuote>> GetQuotesAsync(
        IReadOnlyCollection<int> symbolIds, CancellationToken ct = default);

    /// <summary>
    /// Daily candles for one symbol between <paramref name="from"/> and <paramref name="to"/>,
    /// oldest first. Candles without a usable close are dropped rather than defaulted.
    /// </summary>
    /// <remarks>
    /// One request per symbol: <c>v1/markets/candles</c> takes a single id, unlike quotes and
    /// symbols, which are batched. Callers are expected to fetch these once per session and
    /// cache the result, not once per poll tick.
    /// </remarks>
    Task<IReadOnlyList<QuestradeCandle>> GetDailyCandlesAsync(
        int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// Batched Questrade quote and previous-close REST calls, bearer-authenticated from
/// <see cref="IQuestradeAuthenticator"/>. A 401 means the access token went stale mid-flight
/// (it is only good for 30 minutes) rather than anything wrong with the request itself, so it
/// is handled once with an invalidate-and-retry; any other non-success status is logged and
/// swallowed. A transport-level failure (DNS, connection refused, TLS) is not caught here and
/// propagates to the caller — the worker's own catch-all is what keeps that from killing the
/// loop, not this class.
/// </summary>
public class QuestradeQuoteClient(
    HttpClient http,
    IQuestradeAuthenticator authenticator,
    ILogger<QuestradeQuoteClient> logger) : IQuestradeQuoteClient
{
    public async Task<IReadOnlyDictionary<int, QuestradeQuote>> GetQuotesAsync(
        IReadOnlyCollection<int> symbolIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, QuestradeQuote>();
        if (symbolIds.Count == 0)
        {
            return result;
        }

        const string path = "v1/markets/quotes";
        using var response = await SendWithRetryAsync(path, symbolIds, ct);
        if (response is null)
        {
            return result;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in quotes.EnumerateArray())
        {
            if (!TryGetInt(entry, "symbolId", out var id))
            {
                continue;
            }

            var symbol = TryGetString(entry, "symbol", out var s) ? s : "";
            result[id] = new QuestradeQuote(
                id, symbol,
                ReadDecimal(entry, "lastTradePrice"),
                ReadDecimal(entry, "lastTradePriceTrHrs"),
                ReadLong(entry, "volume"));
        }

        return result;
    }

    public async Task<IReadOnlyList<QuestradeCandle>> GetDailyCandlesAsync(
        int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var path = $"v1/markets/candles/{symbolId}";
        var query = $"?startTime={Uri.EscapeDataString(Iso(from))}"
                    + $"&endTime={Uri.EscapeDataString(Iso(to))}&interval=OneDay";

        using var response = await QuestradeHttp.SendWithRetryAsync(
            http, authenticator, logger, path, session => $"{session.ApiServer}{path}{query}", ct);
        if (response is null)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("candles", out var candles)
            || candles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<QuestradeCandle>();
        foreach (var candle in candles.EnumerateArray())
        {
            // Both halves are required. A candle with a close but no start cannot be placed
            // in a session, and one placed in a session with no close prices nothing.
            if (ReadDateTimeOffset(candle, "start") is { } start
                && ReadDecimal(candle, "close") is { } close)
            {
                result.Add(new QuestradeCandle(start, close));
            }
        }

        return result;
    }

    /// <summary>
    /// Sends the batched id-list GET via the shared <see cref="QuestradeHttp"/> policy: retry exactly
    /// once on a 401 after invalidating the cached session; a second 401 throws — the retry is
    /// bounded, not a loop. Any other non-success status is logged (path and status code only;
    /// the token lives in the header and is never logged) and answered with a null response,
    /// which callers turn into an empty result rather than letting one failed poll take the
    /// worker down.
    /// </summary>
    private Task<HttpResponseMessage?> SendWithRetryAsync(
        string path, IReadOnlyCollection<int> symbolIds, CancellationToken ct) =>
        QuestradeHttp.SendWithRetryAsync(
            http, authenticator, logger, path,
            session => $"{session.ApiServer}{path}?ids={string.Join(',', symbolIds)}", ct);

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        if (element.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Questrade's candle endpoint wants a full ISO 8601 timestamp with an explicit offset —
    /// a bare date is rejected — and the offset must survive round-tripping, so the value is
    /// formatted in its own zone rather than normalised to UTC.
    /// </summary>
    private static string Iso(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            _ => null
        };
    }
}

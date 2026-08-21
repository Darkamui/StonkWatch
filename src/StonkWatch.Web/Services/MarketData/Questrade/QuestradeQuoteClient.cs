using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>One entry from Questrade's <c>v1/markets/quotes</c> batch response.</summary>
public record QuestradeQuote(
    int SymbolId, string Symbol, decimal? LastTradePrice,
    decimal? LastTradePriceTrHrs, long? Volume);

public interface IQuestradeQuoteClient
{
    /// <summary>One request for the whole list — never one call per symbol.</summary>
    Task<IReadOnlyDictionary<int, QuestradeQuote>> GetQuotesAsync(
        IReadOnlyCollection<int> symbolIds, CancellationToken ct = default);

    /// <summary>Reads <c>prevDayClosePrice</c> from the symbol record.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetPreviousClosesAsync(
        IReadOnlyCollection<int> symbolIds, CancellationToken ct = default);
}

/// <summary>
/// Batched Questrade quote and previous-close REST calls, bearer-authenticated from
/// <see cref="IQuestradeAuthenticator"/>. A 401 means the access token went stale mid-flight
/// (it is only good for 30 minutes) rather than anything wrong with the request itself, so it
/// is handled once with an invalidate-and-retry; anything else is logged and swallowed so one
/// bad poll can't kill the worker loop.
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

    public async Task<IReadOnlyDictionary<int, decimal>> GetPreviousClosesAsync(
        IReadOnlyCollection<int> symbolIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, decimal>();
        if (symbolIds.Count == 0)
        {
            return result;
        }

        const string path = "v1/symbols";
        using var response = await SendWithRetryAsync(path, symbolIds, ct);
        if (response is null)
        {
            return result;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in symbols.EnumerateArray())
        {
            if (TryGetInt(entry, "symbolId", out var id) && ReadDecimal(entry, "prevDayClosePrice") is { } close)
            {
                result[id] = close;
            }
        }

        return result;
    }

    /// <summary>
    /// Sends the batched GET, retrying exactly once on a 401 after invalidating the cached
    /// session. A second 401 throws — the retry is bounded, not a loop. Any other non-success
    /// status is logged (path and status code only; the token lives in the header and is
    /// never logged) and answered with a null response, which callers turn into an empty
    /// result rather than letting one failed poll take the worker down.
    /// </summary>
    private async Task<HttpResponseMessage?> SendWithRetryAsync(
        string path, IReadOnlyCollection<int> symbolIds, CancellationToken ct)
    {
        var session = await authenticator.GetSessionAsync(ct);
        var response = await SendAsync(session, path, symbolIds, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            authenticator.Invalidate();

            session = await authenticator.GetSessionAsync(ct);
            response = await SendAsync(session, path, symbolIds, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new HttpRequestException(
                    $"Questrade rejected the access token twice for {path}.",
                    inner: null, HttpStatusCode.Unauthorized);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Questrade request to {Path} failed with {StatusCode}", path, (int)response.StatusCode);
            response.Dispose();
            return null;
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(
        QuestradeSession session, string path, IReadOnlyCollection<int> symbolIds, CancellationToken ct)
    {
        var url = $"{session.ApiServer}{path}?ids={string.Join(',', symbolIds)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        return await http.SendAsync(request, ct);
    }

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

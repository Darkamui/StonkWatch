using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// Maps watchlist tickers to the numeric <c>symbolId</c> Questrade's quote and symbol
/// endpoints key on. Ids are stable for the life of the symbol, so a resolved id is cached
/// for the process lifetime; a ticker that fails to resolve is negative-cached instead, so a
/// delisted symbol left on the watchlist doesn't re-search on every poll forever.
/// </summary>
public interface IQuestradeSymbolResolver
{
    /// <summary>
    /// Maps each ticker to its Questrade symbolId. Tickers that cannot be resolved are
    /// omitted rather than throwing — one bad ticker must not lose a whole poll cycle.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> ResolveAsync(
        IReadOnlyCollection<string> tickers, CancellationToken ct = default);
}

/// <summary>
/// Singleton, thread-safe: touched every poll tick from a single worker, but the concurrent
/// dictionaries make it safe if that ever changes.
/// </summary>
public class QuestradeSymbolResolver(
    HttpClient http,
    IQuestradeAuthenticator authenticator,
    TimeProvider timeProvider,
    ILogger<QuestradeSymbolResolver> logger) : IQuestradeSymbolResolver
{
    private const int NegativeCacheMinutes = 30;

    /// <summary>US equities and ETFs only — a same-ticker TSX listing would otherwise supply
    /// Canadian prices without anything downstream noticing.</summary>
    private static readonly HashSet<string> UsExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "NASDAQ", "NYSE", "AMEX", "ARCA", "BATS", "NYSEMKT"
    };

    private readonly ConcurrentDictionary<string, int> _resolved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negativeUntil = new(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, int>> ResolveAsync(
        IReadOnlyCollection<string> tickers, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var toLookup = new List<string>();
        var now = timeProvider.GetUtcNow();

        foreach (var raw in tickers)
        {
            var ticker = Normalize(raw);
            if (ticker.Length == 0)
            {
                continue;
            }

            if (_resolved.TryGetValue(ticker, out var id))
            {
                result[ticker] = id;
                continue;
            }

            if (_negativeUntil.TryGetValue(ticker, out var until) && now < until)
            {
                // Still within the negative-cache window: skip the network entirely.
                continue;
            }

            toLookup.Add(ticker);
        }

        if (toLookup.Count == 0)
        {
            return result;
        }

        var session = await authenticator.GetSessionAsync(ct);

        foreach (var ticker in toLookup)
        {
            ct.ThrowIfCancellationRequested();

            var id = await LookupAsync(session, ticker, ct);
            if (id is { } resolvedId)
            {
                _resolved[ticker] = resolvedId;
                _negativeUntil.TryRemove(ticker, out _);
                result[ticker] = resolvedId;
            }
            else
            {
                _negativeUntil[ticker] = timeProvider.GetUtcNow() + TimeSpan.FromMinutes(NegativeCacheMinutes);
            }
        }

        return result;
    }

    private async Task<int?> LookupAsync(QuestradeSession session, string ticker, CancellationToken ct)
    {
        const string path = "v1/symbols/search";
        var url = $"{session.ApiServer}{path}?prefix={Uri.EscapeDataString(ticker)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // The token travels in the header, never the URL, so the path alone is safe to log.
            logger.LogWarning(
                "Questrade symbol search failed with {StatusCode} for {Path}",
                (int)response.StatusCode, path);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("symbols", out var symbols)
            || symbols.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in symbols.EnumerateArray())
        {
            if (!TryGetString(entry, "symbol", out var symbol)
                || !string.Equals(symbol, ticker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetString(entry, "listingExchange", out var exchange)
                || !UsExchanges.Contains(exchange))
            {
                continue;
            }

            if (entry.TryGetProperty("symbolId", out var idElement)
                && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt32(out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return value.Length > 0;
        }

        value = "";
        return false;
    }

    private static string Normalize(string ticker) => ticker.Trim().ToUpperInvariant();
}

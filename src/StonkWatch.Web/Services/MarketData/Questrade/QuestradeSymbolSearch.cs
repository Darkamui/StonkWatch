using System.Text.Json;
using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// Backs the sidebar's add-a-symbol box: a prefix goes in, the US listings Questrade matched
/// come back. It hits the same <c>v1/symbols/search</c> endpoint
/// <see cref="QuestradeSymbolResolver"/> does, but wants the opposite shape from it — many
/// candidates for one prefix, uncached — which is why it is its own class rather than another
/// method on the resolver, whose whole design is a two-layer cache around exact-ticker lookups.
/// </summary>
public interface IQuestradeSymbolSearch
{
    /// <summary>
    /// Returns at most <paramref name="limit"/> matches, or an empty list when Questrade
    /// matched nothing. Throws <see cref="HttpRequestException"/> when the search itself
    /// failed: unlike the poller, this is an interactive request, and reporting an upstream
    /// failure as "no matches" would teach the user their symbol does not exist.
    /// </summary>
    Task<IReadOnlyList<SymbolSearchResultDto>> SearchAsync(
        string prefix, int limit, CancellationToken ct = default);
}

public class QuestradeSymbolSearch(
    HttpClient http,
    IQuestradeAuthenticator authenticator,
    IQuestradeSymbolResolver resolver,
    ILogger<QuestradeSymbolSearch> logger) : IQuestradeSymbolSearch
{
    public async Task<IReadOnlyList<SymbolSearchResultDto>> SearchAsync(
        string prefix, int limit, CancellationToken ct = default)
    {
        var query = prefix.Trim();
        if (query.Length == 0 || limit <= 0)
        {
            return [];
        }

        const string path = "v1/symbols/search";

        var response = await QuestradeHttp.SendWithRetryAsync(
            http, authenticator, logger, path,
            session => $"{session.ApiServer}{path}?prefix={Uri.EscapeDataString(query)}", ct);

        if (response is null)
        {
            // QuestradeHttp answers null for any non-success status, having already logged
            // it. The poller reads that as "nothing this tick"; here it has to become an
            // error, or a Questrade outage renders as an empty result list.
            throw new HttpRequestException($"Questrade symbol search for {path} failed.");
        }

        using var _ = response;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("symbols", out var symbols)
            || symbols.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SymbolSearchResultDto>();

        foreach (var entry in symbols.EnumerateArray())
        {
            if (results.Count == limit)
            {
                break;
            }

            if (!TryGetString(entry, "symbol", out var symbol)
                || !TryGetString(entry, "listingExchange", out var exchange)
                || !QuestradeExchanges.Us.Contains(exchange))
            {
                continue;
            }

            if (!entry.TryGetProperty("symbolId", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt32(out var symbolId))
            {
                continue;
            }

            TryGetString(entry, "description", out var description);
            results.Add(new SymbolSearchResultDto(symbol, description, exchange, symbolId));

            // Only the exact-ticker hit is primed, never the prefix neighbours. The resolver's
            // positive cache lives as long as the process, and pouring every symbol starting
            // with "N" into it on the way to typing "NVDA" would grow it without bound from
            // keystrokes. A fully typed ticker is a symbol the user is about to add.
            if (string.Equals(symbol, query, StringComparison.OrdinalIgnoreCase))
            {
                resolver.Prime(symbol, symbolId);
            }
        }

        return results;
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
}

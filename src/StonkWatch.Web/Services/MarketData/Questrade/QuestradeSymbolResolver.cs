using System.Collections.Concurrent;
using System.Text.Json;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// Maps watchlist tickers to the numeric <c>symbolId</c> Questrade's quote and symbol
/// endpoints key on. Ids are stable for the life of the symbol, so a resolved id is cached
/// for the process lifetime; a ticker that a *successful* search genuinely finds nothing for
/// is negative-cached instead, so a delisted symbol left on the watchlist doesn't re-search on
/// every poll forever. A failed search (a stale access token, a 500, a dropped connection) is
/// not evidence the ticker doesn't exist, so it is never negative-cached — see
/// <see cref="LookupOutcome"/>.
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

    /// <summary>
    /// A search either resolves, genuinely finds no matching US listing (safe to negative-cache),
    /// fails transiently on a non-auth status (never safe to negative-cache — the next tick gets
    /// a fresh attempt), or fails on a persistent 401 even after invalidate-and-retry-once — the
    /// one outcome that isn't just this ticker's problem: a token Questrade keeps rejecting will
    /// reject every ticker still to come the same way, so <see cref="ResolveAsync"/> stops
    /// working through the batch rather than rotating the refresh token once per ticker.
    /// </summary>
    private enum LookupOutcome
    {
        Resolved,
        NotFound,
        TransientFailure,
        PersistentAuthFailure
    }

    private readonly record struct LookupResult(LookupOutcome Outcome, int Id = 0)
    {
        public static LookupResult Resolved(int id) => new(LookupOutcome.Resolved, id);
        public static readonly LookupResult NotFound = new(LookupOutcome.NotFound);
        public static readonly LookupResult TransientFailure = new(LookupOutcome.TransientFailure);
        public static readonly LookupResult PersistentAuthFailure = new(LookupOutcome.PersistentAuthFailure);
    }

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

        // Each ticker gets its own session fetch (cheap: the authenticator caches a valid one)
        // and its own invalidate-and-retry-once policy via QuestradeHttp, rather than one
        // session shared across the whole loop — otherwise a token that goes stale partway
        // through a cold-start batch would 401 every ticker still to come.
        for (var i = 0; i < toLookup.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ticker = toLookup[i];

            var lookup = await LookupAsync(ticker, ct);
            switch (lookup.Outcome)
            {
                case LookupOutcome.Resolved:
                    _resolved[ticker] = lookup.Id;
                    _negativeUntil.TryRemove(ticker, out _);
                    result[ticker] = lookup.Id;
                    break;

                case LookupOutcome.NotFound:
                    // A successful search that genuinely found no matching US listing — safe
                    // to negative-cache.
                    _negativeUntil[ticker] = timeProvider.GetUtcNow() + TimeSpan.FromMinutes(NegativeCacheMinutes);
                    break;

                case LookupOutcome.TransientFailure:
                    // Not evidence the ticker doesn't exist — leave it unresolved for this
                    // tick only. QuestradeHttp already logged the status; the next tick (a few
                    // seconds later) retries naturally instead of waiting out a 30-minute
                    // blackout.
                    break;

                case LookupOutcome.PersistentAuthFailure:
                    // A token Questrade rejects twice in a row for one ticker will reject every
                    // other ticker the same way — a per-batch circuit breaker, not a per-ticker
                    // one. Without it, a 50-symbol watchlist against a stuck token would rotate
                    // the refresh token 50 times per tick, every 3 seconds, forever. One
                    // Warning, naming the ticker and how many were abandoned; no token value.
                    var remaining = toLookup.Count - i - 1;
                    logger.LogWarning(
                        "Questrade rejected the access token twice while searching for "
                        + "{Ticker}; abandoning the remaining {Remaining} ticker(s) in this "
                        + "batch rather than retrying every one of them individually.",
                        ticker, remaining);
                    return result;
            }
        }

        return result;
    }

    private async Task<LookupResult> LookupAsync(string ticker, CancellationToken ct)
    {
        const string path = "v1/symbols/search";

        HttpResponseMessage? response;
        try
        {
            response = await QuestradeHttp.SendWithRetryAsync(
                http, authenticator, logger, path,
                session => $"{session.ApiServer}{path}?prefix={Uri.EscapeDataString(ticker)}", ct);
        }
        catch (HttpRequestException)
        {
            // QuestradeHttp throws when a second 401 follows the invalidate-and-retry — a
            // persistently stale token, not evidence this ticker doesn't exist, and not just
            // this ticker's problem either: the same token will fail the same way for
            // everything else still queued. ResolveAsync logs once for the whole batch and
            // stops, rather than every LookupAsync call logging (and every ticker paying) its
            // own invalidate-and-retry.
            return LookupResult.PersistentAuthFailure;
        }

        using var _ = response;
        if (response is null)
        {
            return LookupResult.TransientFailure;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("symbols", out var symbols)
            || symbols.ValueKind != JsonValueKind.Array)
        {
            return LookupResult.NotFound;
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
                return LookupResult.Resolved(id);
            }
        }

        return LookupResult.NotFound;
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

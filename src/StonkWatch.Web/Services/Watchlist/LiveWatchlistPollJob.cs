using Microsoft.Extensions.Options;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.MarketData.Questrade;
using StonkWatch.Web.Services.Monitoring;

namespace StonkWatch.Web.Services.Watchlist;

/// <summary>
/// One poll tick: read the watchlist, resolve tickers to Questrade symbolIds, fetch a batched
/// quote (and, once per session, a previous close), and push the result into
/// <see cref="LiveQuoteCache"/>. Scoped — it owns <see cref="WatchlistService"/>'s DbContext
/// for the duration of the tick.
/// </summary>
public class LiveWatchlistPollJob(
    WatchlistService watchlist,
    IQuestradeSymbolResolver resolver,
    IQuestradeQuoteClient client,
    LiveQuoteCache cache,
    TimeProvider timeProvider,
    IOptions<LiveWatchlistOptions> options,
    ILogger<LiveWatchlistPollJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(ct);
        }
        catch (QuestradeReauthorizationRequiredException ex)
        {
            // Not fatal to the worker: a locked-out user should see stale quotes and a clear
            // log line, not a dead background service. Recovery (Task 8Q's authorize
            // endpoint) takes effect on the next tick without a restart.
            logger.LogError(
                ex,
                "Questrade re-authorization is required; live watchlist quotes will stay "
                + "stale until the user reconnects Questrade.");
        }
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        var maxSymbols = options.Value.MaxSymbols;

        var distinct = (await watchlist.ListSymbolsAsync(ct))
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinct.Count > maxSymbols)
        {
            logger.LogWarning(
                "Watchlist has {Count} symbols, more than the {Max} cap; only the first "
                + "{Max} are polled.", distinct.Count, maxSymbols, maxSymbols);
        }

        var symbols = distinct.Take(maxSymbols).ToList();
        var symbolSet = new HashSet<string>(symbols, StringComparer.Ordinal);

        // Forget anything the cache still holds that fell off the list, so a removed row
        // doesn't linger in the next connection's opening burst.
        foreach (var cached in cache.Snapshot())
        {
            if (!symbolSet.Contains(cached.Symbol))
            {
                cache.Forget(cached.Symbol);
            }
        }

        if (symbols.Count == 0)
        {
            return;
        }

        // Unresolvable tickers are simply absent here — one bad ticker must not lose the
        // whole tick.
        var resolved = await resolver.ResolveAsync(symbols, ct);
        if (resolved.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var session = MarketCalendar.SessionDate(now);
        var regularHours = MarketCalendar.IsOpen(now);

        var needingPreviousClose = cache.SymbolsNeedingPreviousClose(resolved.Keys, session);
        var previousCloseIds = needingPreviousClose
            .Where(resolved.ContainsKey)
            .Select(symbol => resolved[symbol])
            .ToList();

        var previousCloses = previousCloseIds.Count > 0
            ? await client.GetPreviousClosesAsync(previousCloseIds, ct)
            : new Dictionary<int, decimal>();

        var idToSymbol = resolved
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);

        var quotes = await client.GetQuotesAsync(resolved.Values.ToList(), ct);

        foreach (var (id, quote) in quotes)
        {
            if (!idToSymbol.TryGetValue(id, out var symbol))
            {
                continue;
            }

            var price = regularHours ? quote.LastTradePriceTrHrs : quote.LastTradePrice;
            if (price is null)
            {
                // A zero would render as a real number and compute a -100% change, so a
                // missing price field is skipped rather than defaulted.
                continue;
            }

            decimal? extendedPrice = null;
            DateTimeOffset? extendedAt = null;
            // quote.LastTradePrice cannot be null here: !regularHours means price was assigned
            // from it just above, and a null price already `continue`d before this point.
            if (!regularHours && quote.LastTradePrice != quote.LastTradePriceTrHrs)
            {
                // Set together or not at all: LiveQuoteCache.Merge judges freshness by
                // ExtendedAt, so a price without its own timestamp would mislabel when it
                // happened.
                extendedPrice = quote.LastTradePrice;
                extendedAt = now;
            }

            var previousClose = previousCloses.TryGetValue(id, out var pc) ? pc : (decimal?)null;

            var mapped = new Quote(
                symbol, price.Value, now,
                quote.Volume, previousClose, extendedPrice, extendedAt);

            cache.ApplySnapshot(mapped, session);
        }
    }
}

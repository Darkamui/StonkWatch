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
    /// <summary>How far back to ask for daily candles — see FetchPreviousClosesAsync.</summary>
    private const int CandleLookbackDays = 14;

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

        // The session on screen, not today's calendar date. Before the opening bell every row
        // still shows the previous session's close and change, so that is the session whose
        // baseline the cache needs; rolling at midnight would swap the baseline nine hours
        // early and flatten every change percentage to 0.00% for the whole of pre-market.
        var session = MarketCalendar.DisplaySession(now);
        var regularHours = MarketCalendar.IsOpen(now);

        var previousCloses = await FetchPreviousClosesAsync(
            cache.SymbolsNeedingPreviousClose(resolved.Keys, session), resolved, session, ct);

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

            // LastTradePriceTrHrs is the last *trading hours* print, which is what Last shows
            // in every phase: the live price during the session, that session's closing price
            // once it is over. It is never the extended-hours print — a 04:15 trade belongs in
            // the Ext column, not in Last. The fallback covers a symbol Questrade has no
            // trading-hours print for at all, where some price beats an em dash.
            var regularClose = Usable(quote.LastTradePriceTrHrs);
            var price = regularClose ?? Usable(quote.LastTradePrice);
            if (price is null)
            {
                continue;
            }

            decimal? extendedPrice = null;
            DateTimeOffset? extendedAt = null;
            // Only outside the session, and only when there is a real regular close to measure
            // against — LiveQuote.ExtendedChangePercent divides by Last, so a Last that fell
            // back to the extended print itself would report a flat 0.00% move. An extended
            // price equal to the close means nothing has traded since the bell, which is an
            // em dash rather than a 0.00%.
            if (!regularHours
                && regularClose is { } close
                && quote.LastTradePrice is { } lastTrade
                && lastTrade != close)
            {
                // Set together or not at all: LiveQuoteCache.Merge judges freshness by
                // ExtendedAt, so a price without its own timestamp would mislabel when it
                // happened.
                extendedPrice = lastTrade;
                extendedAt = now;
            }

            var previousClose = previousCloses.TryGetValue(symbol, out var pc) ? pc : (decimal?)null;

            var mapped = new Quote(
                symbol, price.Value, now, quote.Volume, previousClose, extendedPrice, extendedAt);

            cache.ApplySnapshot(mapped, session);
        }
    }

    /// <summary>
    /// A price Questrade actually has, or null. Zero is not a price: it is what the quote
    /// carries for a field with nothing behind it, and left alone it renders as a real number
    /// and computes a -100% change.
    /// </summary>
    private static decimal? Usable(decimal? price) => price is { } value && value != 0 ? value : null;

    /// <summary>
    /// The close of the last regular session before <paramref name="session"/> — the baseline
    /// the displayed change percentage is measured against — keyed by ticker.
    /// </summary>
    /// <remarks>
    /// Deliberately not Questrade's <c>prevDayClosePrice</c>, which both the quote and symbol
    /// endpoints hand over for free in one batched call. That field rolls forward as soon as a
    /// new trading day begins, so from pre-market onwards it reports the very close the row is
    /// already showing as Last, and the change computes to exactly 0.00% until the opening
    /// bell. Daily candles are the only Questrade source that reaches back the extra session.
    ///
    /// They cost one request per symbol, so this runs only for the symbols whose cached
    /// baseline is missing or belongs to an earlier session — in steady state, once a day at
    /// 09:30, when <see cref="MarketCalendar.DisplaySession"/> rolls over. Sequential, like
    /// the resolver's own per-ticker lookups: the authenticator behind these calls is shared,
    /// and a cold start's worth of parallel requests is not worth making it contend.
    /// </remarks>
    private async Task<Dictionary<string, decimal>> FetchPreviousClosesAsync(
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, int> resolved,
        DateOnly session,
        CancellationToken ct)
    {
        var closes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (symbols.Count == 0)
        {
            return closes;
        }

        var sessionStart = MarketCalendar.SessionStart(session);

        // Far enough back to clear the longest run of non-trading days a US calendar produces
        // — a holiday abutting a weekend — several times over. Only the last usable candle is
        // read, so asking for extra days costs nothing but a slightly larger response.
        var from = sessionStart.AddDays(-CandleLookbackDays);

        foreach (var symbol in symbols)
        {
            if (!resolved.TryGetValue(symbol, out var id))
            {
                continue;
            }

            var candles = await client.GetDailyCandlesAsync(id, from, sessionStart, ct);

            // Filtered on the candle's own start rather than trusting the endTime bound to be
            // exclusive: a daily candle spans midnight to midnight, so one that merely touches
            // the boundary is the session on screen, whose close is already Last. Including it
            // would measure that session against itself.
            var baseline = candles.LastOrDefault(candle => candle.Start < sessionStart);
            if (baseline is not null)
            {
                closes[symbol] = baseline.Close;
            }
            else
            {
                // No exception and no default: the row simply renders an em dash for Chg%
                // until a later tick finds history, which is the honest answer for a symbol
                // with no full session behind it.
                logger.LogDebug(
                    "No daily candle before {Session} for {Symbol}; its change percentage "
                    + "stays blank.", session, symbol);
            }
        }

        return closes;
    }
}

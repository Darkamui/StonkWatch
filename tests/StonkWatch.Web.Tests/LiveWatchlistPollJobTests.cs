using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.MarketData.Questrade;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Tests;

/// <summary>
/// One poll tick: watchlist symbols in, resolved ids, a batched quote call, and a mapped
/// <see cref="Quote"/> per symbol into <see cref="LiveQuoteCache"/>. These tests pin the
/// field-mapping rules (Last is always the regular-session print, the extended print goes to
/// Ext, the extended pair is set together or not at all), the once-per-session baseline fetch
/// and which session it is keyed on, and the failure isolation the brief calls out — one bad
/// ticker, a null price, or Questrade locking the user out must never lose the rest of a tick.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LiveWatchlistPollJobTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Tuesday 18 August 2026, a normal trading day (no holiday).
    private static readonly DateTimeOffset RegularHours = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero); // 11:00 EDT
    private static readonly DateTimeOffset AfterHours = new(2026, 8, 18, 22, 0, 0, TimeSpan.Zero);    // 18:00 EDT
    private static readonly DateTimeOffset PreMarket = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);     // 08:00 EDT

    /// <summary>
    /// Midnight Eastern on Monday 17 August 2026 — the session on screen throughout
    /// <see cref="PreMarket"/>, since Tuesday's has not opened yet.
    /// </summary>
    private static readonly DateTimeOffset MondayStart =
        new(2026, 8, 17, 0, 0, 0, TimeSpan.FromHours(-4));

    // Started at the earliest of the three: FakeTimeProvider refuses to go backwards, and
    // every test sets the phase it needs before running the job.
    private readonly FakeTimeProvider _time = new(PreMarket);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test doubles -------------------------------------------------------------------

    private sealed class FakeResolver(Dictionary<string, int> map) : IQuestradeSymbolResolver
    {
        public List<IReadOnlyCollection<string>> Calls { get; } = [];

        public Task<IReadOnlyDictionary<string, int>> ResolveAsync(
            IReadOnlyCollection<string> tickers, CancellationToken ct = default)
        {
            Calls.Add(tickers);
            var result = new Dictionary<string, int>();
            foreach (var ticker in tickers)
            {
                if (map.TryGetValue(ticker, out var id))
                {
                    result[ticker] = id;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, int>>(result);
        }

        // These fakes exercise the poll path, which never primes. Nothing to record.
        public void Prime(string ticker, int symbolId)
        {
        }
    }

    private sealed class ThrowingResolver : IQuestradeSymbolResolver
    {
        public Task<IReadOnlyDictionary<string, int>> ResolveAsync(
            IReadOnlyCollection<string> tickers, CancellationToken ct = default) =>
            throw new QuestradeReauthorizationRequiredException(
                "Questrade rejected the stored refresh token. Re-authorize StonkWatch.");

        // These fakes exercise the poll path, which never primes. Nothing to record.
        public void Prime(string ticker, int symbolId)
        {
        }
    }

    private sealed class FakeQuoteClient : IQuestradeQuoteClient
    {
        public Dictionary<int, QuestradeQuote> Quotes { get; init; } = [];
        /// <summary>
        /// The baseline close each symbol should resolve to. Served as a single daily candle
        /// starting the day before whatever session the job asks about, which is the shape a
        /// real response has — <see cref="Candles"/> overrides it for the tests that care
        /// about candle selection itself.
        /// </summary>
        public Dictionary<int, decimal> PreviousCloses { get; init; } = [];

        public Dictionary<int, IReadOnlyList<QuestradeCandle>> Candles { get; init; } = [];
        public List<IReadOnlyCollection<int>> QuoteCalls { get; } = [];
        public List<(int SymbolId, DateTimeOffset From, DateTimeOffset To)> CandleCalls { get; } = [];

        public Task<IReadOnlyDictionary<int, QuestradeQuote>> GetQuotesAsync(
            IReadOnlyCollection<int> symbolIds, CancellationToken ct = default)
        {
            QuoteCalls.Add(symbolIds);
            var result = symbolIds
                .Where(Quotes.ContainsKey)
                .ToDictionary(id => id, id => Quotes[id]);
            return Task.FromResult<IReadOnlyDictionary<int, QuestradeQuote>>(result);
        }

        public Task<IReadOnlyList<QuestradeCandle>> GetDailyCandlesAsync(
            int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            CandleCalls.Add((symbolId, from, to));

            if (Candles.TryGetValue(symbolId, out var candles))
            {
                return Task.FromResult(candles);
            }

            IReadOnlyList<QuestradeCandle> synthesized = PreviousCloses.TryGetValue(symbolId, out var close)
                ? [new QuestradeCandle(to.AddDays(-1), close)]
                : [];
            return Task.FromResult(synthesized);
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private (WatchlistService Service, StonkWatchDbContext Db) NewWatchlistService(int maxSymbols = 50)
    {
        var db = fixture.CreateContext();
        var options = Options.Create(new LiveWatchlistOptions { MaxSymbols = maxSymbols });
        return (new WatchlistService(db, _time, options), db);
    }

    private LiveWatchlistPollJob NewJob(
        WatchlistService watchlist,
        IQuestradeSymbolResolver resolver,
        IQuestradeQuoteClient client,
        LiveQuoteCache cache,
        ILogger<LiveWatchlistPollJob>? logger = null,
        int maxSymbols = 50) =>
        new(watchlist, resolver, client, cache, _time,
            Options.Create(new LiveWatchlistOptions { MaxSymbols = maxSymbols }),
            logger ?? NullLogger<LiveWatchlistPollJob>.Instance);

    // ---- tests --------------------------------------------------------------------------

    [Fact]
    public async Task Regular_hours_uses_lastTradePriceTrHrs_and_sets_no_extended_price()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, 1_000_000)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(150.50m, quote!.Last);
        Assert.Null(quote.ExtendedPrice);
        Assert.Null(quote.ExtendedAt);
        Assert.Equal(1_000_000L, quote.Volume);
    }

    [Fact]
    public async Task Outside_regular_hours_Last_holds_the_close_and_the_extended_print_goes_to_Ext()
    {
        _time.SetUtcNow(AfterHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 151.25m, 150.50m, 1_000_000)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        // Last is the closing print, not the 151.25 that traded after the bell: after hours
        // the row freezes the session that just ended and reports the move since it separately.
        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(150.50m, quote!.Last);
        Assert.Equal(151.25m, quote.ExtendedPrice);
        Assert.Equal(AfterHours, quote.ExtendedAt);
        Assert.Equal(0.50m, Math.Round(quote.ExtendedChangePercent!.Value, 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public async Task Outside_regular_hours_without_a_close_Last_falls_back_to_the_extended_print(
        double? regularHoursPrice)
    {
        _time.SetUtcNow(AfterHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 151.25m, (decimal?)regularHoursPrice, 1_000_000)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        // With no regular-session print to show, some price beats an em dash, so the extended
        // one becomes Last. Ext then has nothing left to measure against — it would be the
        // price against itself, a flat 0.00% — so the pair is not set at all.
        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(151.25m, quote!.Last);
        Assert.Null(quote.ExtendedPrice);
        Assert.Null(quote.ExtendedAt);
        Assert.Null(quote.ExtendedChangePercent);
    }

    [Fact]
    public async Task Outside_regular_hours_with_equal_prices_sets_no_extended_price()
    {
        _time.SetUtcNow(AfterHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.50m, 150.50m, 1_000_000)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(150.50m, quote!.Last);
        Assert.Null(quote.ExtendedPrice);
        Assert.Null(quote.ExtendedAt);
    }

    [Fact]
    public async Task A_previous_close_is_fetched_once_per_session()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, 1_000_000)
            },
            PreviousCloses = new Dictionary<int, decimal> { [1] = 148.00m }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();
        await job.RunAsync();

        Assert.Single(client.CandleCalls);
        Assert.Equal(148.00m, cache.Get("AAPL")!.PreviousClose);
    }

    [Fact]
    public async Task In_pre_market_the_baseline_is_the_close_before_the_session_on_screen()
    {
        _time.SetUtcNow(PreMarket);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["ASTS"] = 1 });
        var client = new FakeQuoteClient
        {
            // 67.11 pre-market, off a 68.65 close on Monday.
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "ASTS", 67.11m, 68.65m, 42_950)
            },
            Candles = new Dictionary<int, IReadOnlyList<QuestradeCandle>>
            {
                [1] =
                [
                    new QuestradeCandle(MondayStart.AddDays(-3), 65.06m),

                    // Friday's close: the last one starting before Monday, so the one Monday's
                    // +5.52% is measured against.
                    new QuestradeCandle(MondayStart.AddDays(-1), 65.06m)
                ]
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        // Tuesday has not opened, so the whole row still reports Monday: its close as Last,
        // its own session move as Chg%, and the pre-market drift as Ext.
        Assert.Equal(MondayStart, Assert.Single(client.CandleCalls).To);

        var quote = cache.Get("ASTS");
        Assert.NotNull(quote);
        Assert.Equal(68.65m, quote!.Last);
        Assert.Equal(65.06m, quote.PreviousClose);
        Assert.Equal(new DateOnly(2026, 8, 17), quote.PreviousCloseSession);
        Assert.Equal(5.52m, Math.Round(quote.ChangePercent!.Value, 2));
        Assert.Equal(-2.24m, Math.Round(quote.ExtendedChangePercent!.Value, 2));
    }

    [Fact]
    public async Task A_candle_that_starts_on_the_displayed_session_is_not_the_baseline()
    {
        _time.SetUtcNow(PreMarket);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 151.25m, 150.50m, 1_000_000)
            },
            Candles = new Dictionary<int, IReadOnlyList<QuestradeCandle>>
            {
                [1] =
                [
                    new QuestradeCandle(MondayStart.AddDays(-1), 148.00m),

                    // Monday's own candle. Questrade's endTime bound is not documented as
                    // exclusive, so this can come back; using it would measure the session
                    // against itself and report a flat 0.00%.
                    new QuestradeCandle(MondayStart, 150.50m)
                ]
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        Assert.Equal(148.00m, cache.Get("AAPL")!.PreviousClose);
    }

    [Fact]
    public async Task No_candle_history_leaves_the_change_percentage_blank()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, 1_000_000)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        // A newly listed symbol has no full session behind it. The price still renders; the
        // change is null rather than a fabricated 0.00%.
        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(150.50m, quote!.Last);
        Assert.Null(quote.PreviousClose);
        Assert.Null(quote.ChangePercent);
    }

    [Fact]
    public async Task A_symbol_removed_from_the_watchlist_is_forgotten()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        var item = await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, null)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();
        Assert.NotNull(cache.Get("AAPL"));

        await watchlist.RemoveItemAsync(item.Id);
        await job.RunAsync();

        Assert.DoesNotContain(cache.Snapshot(), q => q.Symbol == "AAPL");
    }

    [Fact]
    public async Task A_symbol_that_does_not_resolve_does_not_stop_the_others()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("BADCO"));
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        // BADCO deliberately absent from the resolver's map.
        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, null)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        Assert.NotNull(cache.Get("AAPL"));
        Assert.Null(cache.Get("BADCO"));
    }

    [Fact]
    public async Task A_quote_with_no_price_at_all_is_skipped()
    {
        // Neither field has a price. lastTradePriceTrHrs is what Last shows and lastTradePrice
        // is its fallback, so a row only disappears when Questrade has neither.
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", null, null, null)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        Assert.Null(cache.Get("AAPL"));
    }

    [Fact]
    public async Task A_zero_price_counts_as_no_price()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 0m, 0m, null)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        // Questrade returns zero for a field with nothing behind it. Stored, it renders as a
        // real price and computes a -100% change against any baseline.
        Assert.Null(cache.Get("AAPL"));
    }

    [Fact]
    public async Task Reauthorization_required_is_logged_and_does_not_escape_the_job()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new ThrowingResolver();
        var client = new FakeQuoteClient();
        var cache = new LiveQuoteCache(_time);
        var log = new CapturingLogger<LiveWatchlistPollJob>();
        var job = NewJob(watchlist, resolver, client, cache, log);

        // Must not throw.
        await job.RunAsync();

        var error = Assert.Single(log.AtLevel(LogLevel.Error));
        Assert.Contains("re-authoriz", error.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_batched_quote_call_covers_the_whole_watchlist()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("MSFT"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1, ["MSFT"] = 2 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, 1),
                [2] = new QuestradeQuote(2, "MSFT", 300.00m, 301.00m, 2)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        var call = Assert.Single(client.QuoteCalls);
        Assert.Equal([1, 2], call.OrderBy(x => x));
    }

    [Fact]
    public async Task The_MaxSymbols_cap_truncates_the_list_and_logs_exactly_one_warning_per_tick()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService(); // default cap 50 — allows adding all three
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("MSFT"));
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("GOOG"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1, ["MSFT"] = 2, ["GOOG"] = 3 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, 150.50m, 1),
                [2] = new QuestradeQuote(2, "MSFT", 300.00m, 301.00m, 2),
                [3] = new QuestradeQuote(3, "GOOG", 140.00m, 141.00m, 3)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var log = new CapturingLogger<LiveWatchlistPollJob>();
        // The job's own cap (2), independent of the watchlist's own cap used above to allow
        // adding all three rows in the first place.
        var job = NewJob(watchlist, resolver, client, cache, log, maxSymbols: 2);

        await job.RunAsync();

        var call = Assert.Single(resolver.Calls);
        Assert.Equal(2, call.Count);
        Assert.Single(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task An_empty_watchlist_does_not_call_the_resolver_or_the_client()
    {
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;

        var resolver = new FakeResolver([]);
        var client = new FakeQuoteClient();
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

        Assert.Empty(resolver.Calls);
        Assert.Empty(client.QuoteCalls);
    }
}

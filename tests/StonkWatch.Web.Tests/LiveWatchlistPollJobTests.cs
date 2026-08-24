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
/// field-mapping rules from the design delta (regular vs. extended-hours price source, the
/// extended pair being set together or not at all), the once-per-session previous-close fetch,
/// and the failure isolation the brief calls out — one bad ticker, a null price, or Questrade
/// locking the user out must never lose the rest of the tick.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LiveWatchlistPollJobTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Tuesday 18 August 2026, a normal trading day (no holiday).
    private static readonly DateTimeOffset RegularHours = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero); // 11:00 EDT
    private static readonly DateTimeOffset AfterHours = new(2026, 8, 18, 22, 0, 0, TimeSpan.Zero);    // 18:00 EDT

    private readonly FakeTimeProvider _time = new(RegularHours);

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
        public Dictionary<int, decimal> PreviousCloses { get; init; } = [];
        public List<IReadOnlyCollection<int>> QuoteCalls { get; } = [];
        public List<IReadOnlyCollection<int>> PreviousCloseCalls { get; } = [];

        public Task<IReadOnlyDictionary<int, QuestradeQuote>> GetQuotesAsync(
            IReadOnlyCollection<int> symbolIds, CancellationToken ct = default)
        {
            QuoteCalls.Add(symbolIds);
            var result = symbolIds
                .Where(Quotes.ContainsKey)
                .ToDictionary(id => id, id => Quotes[id]);
            return Task.FromResult<IReadOnlyDictionary<int, QuestradeQuote>>(result);
        }

        public Task<IReadOnlyDictionary<int, decimal>> GetPreviousClosesAsync(
            IReadOnlyCollection<int> symbolIds, CancellationToken ct = default)
        {
            PreviousCloseCalls.Add(symbolIds);
            var result = symbolIds
                .Where(PreviousCloses.ContainsKey)
                .ToDictionary(id => id, id => PreviousCloses[id]);
            return Task.FromResult<IReadOnlyDictionary<int, decimal>>(result);
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
        Assert.Null(quote.RegularClose);
        Assert.Equal(1_000_000L, quote.Volume);
    }

    [Fact]
    public async Task Outside_regular_hours_uses_lastTradePrice_and_sets_the_extended_trio()
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

        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(151.25m, quote!.Last);
        Assert.NotNull(quote.ExtendedPrice);
        Assert.NotNull(quote.ExtendedAt);
        Assert.Equal(151.25m, quote.ExtendedPrice);
        Assert.Equal(AfterHours, quote.ExtendedAt);

        // lastTradePriceTrHrs is the last regular-session print, which is what the Ext
        // percentage is measured against — 151.25 off a 150.50 close is +0.50%.
        Assert.Equal(150.50m, quote.RegularClose);
        Assert.Equal(0.50m, Math.Round(quote.ExtendedChangePercent!.Value, 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public async Task Outside_regular_hours_a_missing_baseline_sets_no_extended_trio(
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

        // The extended print itself is fine and still becomes Last; only Ext is unrenderable.
        // A percentage over a null or zero denominator is either nothing or nonsense, and the
        // trio moves as a unit, so none of it is stored.
        var quote = cache.Get("AAPL");
        Assert.NotNull(quote);
        Assert.Equal(151.25m, quote!.Last);
        Assert.Null(quote.ExtendedPrice);
        Assert.Null(quote.ExtendedAt);
        Assert.Null(quote.RegularClose);
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
        Assert.Null(quote.RegularClose);
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

        Assert.Single(client.PreviousCloseCalls);
        Assert.Equal(148.00m, cache.Get("AAPL")!.PreviousClose);
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
    public async Task A_quote_with_a_null_price_is_skipped()
    {
        // Regular hours reads lastTradePriceTrHrs, which is null here.
        _time.SetUtcNow(RegularHours);
        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var resolver = new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 });
        var client = new FakeQuoteClient
        {
            Quotes = new Dictionary<int, QuestradeQuote>
            {
                [1] = new QuestradeQuote(1, "AAPL", 150.00m, null, null)
            }
        };
        var cache = new LiveQuoteCache(_time);
        var job = NewJob(watchlist, resolver, client, cache);

        await job.RunAsync();

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

using Microsoft.Extensions.DependencyInjection;
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
/// The worker is deliberately thin — a <see cref="PeriodicTimer"/> that either runs a tick or
/// skips it — but the skip decision has two ways to go wrong that are easy to miss in review:
/// skipping every tick all night must not also let the Questrade refresh token die from
/// disuse, and the keepalive path that prevents that must not itself fire on every skipped
/// tick (it would defeat the point of skipping in the first place).
/// </summary>
[Collection(PostgresCollection.Name)]
public class LiveWatchlistPollWorkerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Sunday 16 August 2026, 18:00 ET — closed, and not close to any boundary.</summary>
    private static readonly DateTimeOffset SundayEvening = new(2026, 8, 16, 22, 0, 0, TimeSpan.Zero);

    /// <summary>Tuesday 18 August 2026, 18:00 ET — inside the after-hours session.</summary>
    private static readonly DateTimeOffset TuesdayAfterHours = new(2026, 8, 18, 22, 0, 0, TimeSpan.Zero);

    /// <summary>Monday 17 August 2026, 03:56 ET — four minutes before the pre-market opens.</summary>
    private static readonly DateTimeOffset JustBeforePreMarket = new(2026, 8, 17, 7, 56, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test doubles ---------------------------------------------------------------------

    /// <summary>
    /// Fails the test immediately if the worker ever asks for a scope while there are no
    /// subscribers — the whole point of the skip branch is that a scoped job (and the
    /// DbContext underneath it) is never even resolved when nobody is watching.
    /// </summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException(
                "The worker must not touch the scope factory while there are no subscribers.");
    }

    private sealed class CountingAuthenticator : IQuestradeAuthenticator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new QuestradeSession("token", "https://api.test/", DateTimeOffset.MaxValue));
        }

        public void Invalidate()
        {
        }
    }

    private sealed class FakeResolver(Dictionary<string, int> map) : IQuestradeSymbolResolver
    {
        public Task<IReadOnlyDictionary<string, int>> ResolveAsync(
            IReadOnlyCollection<string> tickers, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(
                tickers.Where(map.ContainsKey).ToDictionary(t => t, t => map[t]));

        // These fakes exercise the poll path, which never primes. Nothing to record.
        public void Prime(string ticker, int symbolId)
        {
        }
    }

    private sealed class CountingQuoteClient : IQuestradeQuoteClient
    {
        private int _quoteCalls;

        public int QuoteCalls => Volatile.Read(ref _quoteCalls);

        public Task<IReadOnlyDictionary<int, QuestradeQuote>> GetQuotesAsync(
            IReadOnlyCollection<int> symbolIds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _quoteCalls);
            var result = symbolIds.ToDictionary(id => id, id => new QuestradeQuote(id, "AAPL", 150m, 150m, 1));
            return Task.FromResult<IReadOnlyDictionary<int, QuestradeQuote>>(result);
        }

        public Task<IReadOnlyDictionary<int, decimal>> GetPreviousClosesAsync(
            IReadOnlyCollection<int> symbolIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, decimal>>(new Dictionary<int, decimal>());
    }

    /// <summary>
    /// Wraps a real scope factory but throws on its first call only — simulates the DI
    /// container itself failing to build a tick's scope (e.g. a transient resource exhaustion),
    /// which is a different failure point than anything inside the job.
    /// </summary>
    private sealed class FlakyScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("Simulated scope-creation failure on the first tick.");
            }

            return inner.CreateScope();
        }
    }

    /// <summary>
    /// Throws an exception the job's own catch (QuestradeReauthorizationRequiredException only)
    /// does not anticipate on the first call, then resolves normally — models a failure mode
    /// inside the tick itself, as opposed to <see cref="FlakyScopeFactory"/>'s failure to even
    /// start one.
    /// </summary>
    private sealed class ThrowOnceResolver(Dictionary<string, int> map) : IQuestradeSymbolResolver
    {
        private int _calls;

        public Task<IReadOnlyDictionary<string, int>> ResolveAsync(
            IReadOnlyCollection<string> tickers, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("Simulated resolver failure on the first tick.");
            }

            return Task.FromResult<IReadOnlyDictionary<string, int>>(
                tickers.Where(map.ContainsKey).ToDictionary(t => t, t => map[t]));
        }

        // These fakes exercise the poll path, which never primes. Nothing to record.
        public void Prime(string ticker, int symbolId)
        {
        }
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Condition was not met within the timeout.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Advances the fake clock one poll interval at a time, yielding after each step so the
    /// worker's background loop — which is driven by continuations on that same
    /// <see cref="FakeTimeProvider"/> — gets a chance to observe the tick and re-arm the timer
    /// for the next one. A single large jump would only fire the one timer already pending,
    /// not the several ticks in between.
    /// </summary>
    private static async Task AdvanceInStepsAsync(
        FakeTimeProvider time, TimeSpan step, int steps, int settleMs = 5)
    {
        for (var i = 0; i < steps; i++)
        {
            time.Advance(step);
            await Task.Delay(settleMs);
        }
    }

    /// <summary>
    /// Advances one interval and waits for the poll it fires to reach the quote client.
    /// Deterministic where <see cref="AdvanceInStepsAsync"/> is not: a
    /// <see cref="FakeTimeProvider"/> only fires timers already armed, so advancing again
    /// before the loop has re-armed silently loses a tick — and a tick that polls does a
    /// database round trip, which outlasts any fixed settle delay often enough to matter.
    /// Only usable for ticks that are expected to poll; a skipped tick leaves no trace to
    /// wait on.
    /// </summary>
    private static async Task AdvanceAndAwaitPollAsync(
        FakeTimeProvider time, TimeSpan step, CountingQuoteClient quotes)
    {
        var before = quotes.QuoteCalls;
        time.Advance(step);
        await WaitUntilAsync(() => quotes.QuoteCalls > before, TimeSpan.FromSeconds(10));
    }

    // ---- tests --------------------------------------------------------------------------

    [Fact]
    public async Task An_idle_worker_still_refreshes_the_token_before_it_can_expire()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new LiveQuoteCache(time); // never subscribed: SubscriberCount stays 0
        var auth = new CountingAuthenticator();

        // An artificially long interval (real config caps PollSeconds at 60) keeps this test
        // to a handful of loop iterations instead of the ~14,400 real 3-second ticks it would
        // take to cross 12 hours; the keepalive logic being exercised only cares about the
        // gap between ticks, not the literal configured value.
        var options = Options.Create(new LiveWatchlistOptions { PollSeconds = 3600 });
        var worker = new LiveWatchlistPollWorker(
            new ThrowingScopeFactory(), cache, auth, time, options,
            NullLogger<LiveWatchlistPollWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // 14 ticks an hour apart: the first tick (nothing touched yet) must refresh
            // immediately, and by the 13th/14th tick more than 12 hours have passed since,
            // so a second refresh must have happened by the time this loop ends.
            await AdvanceInStepsAsync(time, TimeSpan.FromHours(1), 14);
            await WaitUntilAsync(() => auth.Calls >= 2, TimeSpan.FromSeconds(10));

            // Exactly 2: the first-tick touch (hour 1, nothing touched yet) and the re-fire
            // once more than 12 hours have elapsed since (hour 13). A looser bound here would
            // let an off-by-one in the window arithmetic — or a change that halves
            // TokenKeepaliveHours — through undetected.
            Assert.Equal(2, auth.Calls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_skipped_tick_does_not_refresh_again_before_the_keepalive_window_elapses()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new LiveQuoteCache(time);
        var auth = new CountingAuthenticator();
        var options = Options.Create(new LiveWatchlistOptions { PollSeconds = 3600 });
        var worker = new LiveWatchlistPollWorker(
            new ThrowingScopeFactory(), cache, auth, time, options,
            NullLogger<LiveWatchlistPollWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Comfortably inside the 12-hour keepalive window: only the first, immediate
            // refresh should have happened.
            await AdvanceInStepsAsync(time, TimeSpan.FromHours(1), 5);
            await WaitUntilAsync(() => auth.Calls >= 1, TimeSpan.FromSeconds(10));

            Assert.Equal(1, auth.Calls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_subscribed_worker_polls_and_stops_when_the_last_subscriber_leaves()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new LiveQuoteCache(time);
        var auth = new CountingAuthenticator();

        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var quoteClient = new CountingQuoteClient();
        var job = new LiveWatchlistPollJob(
            watchlist, new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 }), quoteClient,
            cache, time, Options.Create(new LiveWatchlistOptions()),
            NullLogger<LiveWatchlistPollJob>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(job);
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new LiveWatchlistOptions { PollSeconds = 3 });
        var worker = new LiveWatchlistPollWorker(
            scopeFactory, cache, auth, time, options,
            NullLogger<LiveWatchlistPollWorker>.Instance);

        // A live subscription — GetAsyncEnumerator registers the subscriber even before the
        // first MoveNextAsync, matching how the SSE endpoint holds the sidebar's connection
        // open. Disposing it is how "the last subscriber leaves" is simulated below.
        var subscription = cache.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.Equal(1, cache.SubscriberCount);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(3), 3);
            await WaitUntilAsync(() => quoteClient.QuoteCalls > 0, TimeSpan.FromSeconds(10));
            Assert.True(quoteClient.QuoteCalls > 0);

            await subscription.DisposeAsync();
            Assert.Equal(0, cache.SubscriberCount);

            var callsAtDrop = quoteClient.QuoteCalls;
            // Several more ticks with nobody subscribed: the count must stop climbing. If the
            // worker kept polling after the last subscriber left, this would fail.
            await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(3), 5);
            await Task.Delay(50);
            Assert.Equal(callsAtDrop, quoteClient.QuoteCalls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ---------- I-1 / I-2 fix: one bad tick must not stop the ones after it ----------

    [Fact]
    public async Task A_scope_creation_failure_on_one_tick_does_not_stop_the_worker()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new LiveQuoteCache(time);
        var auth = new CountingAuthenticator();

        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var quoteClient = new CountingQuoteClient();
        var job = new LiveWatchlistPollJob(
            watchlist, new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 }), quoteClient,
            cache, time, Options.Create(new LiveWatchlistOptions()),
            NullLogger<LiveWatchlistPollJob>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(job);
        await using var provider = services.BuildServiceProvider();
        var flakyFactory = new FlakyScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());

        var log = new CapturingLogger<LiveWatchlistPollWorker>();
        var options = Options.Create(new LiveWatchlistOptions { PollSeconds = 3 });
        var worker = new LiveWatchlistPollWorker(flakyFactory, cache, auth, time, options, log);

        var subscription = cache.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The first tick's CreateScope() throws before the job even runs; a later tick
            // must still reach the quote client rather than the loop dying silently.
            await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(3), 4);
            await WaitUntilAsync(() => quoteClient.QuoteCalls > 0, TimeSpan.FromSeconds(10));

            Assert.True(quoteClient.QuoteCalls > 0, "A later tick must still reach the quote client.");
            Assert.True(flakyFactory.Calls >= 2, "The failing first tick must not stop later ticks from trying again.");
            Assert.NotEmpty(log.AtLevel(LogLevel.Error));
        }
        finally
        {
            await subscription.DisposeAsync();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_tick_that_throws_something_the_job_did_not_anticipate_does_not_stop_the_worker()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new LiveQuoteCache(time);
        var auth = new CountingAuthenticator();

        var (watchlist, db) = NewWatchlistService();
        await using var _ = db;
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var quoteClient = new CountingQuoteClient();
        var job = new LiveWatchlistPollJob(
            watchlist, new ThrowOnceResolver(new Dictionary<string, int> { ["AAPL"] = 1 }), quoteClient,
            cache, time, Options.Create(new LiveWatchlistOptions()),
            NullLogger<LiveWatchlistPollJob>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(job);
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var log = new CapturingLogger<LiveWatchlistPollWorker>();
        var options = Options.Create(new LiveWatchlistOptions { PollSeconds = 3 });
        var worker = new LiveWatchlistPollWorker(scopeFactory, cache, auth, time, options, log);

        var subscription = cache.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The first tick's resolver throws an exception the job's own catch does not
            // anticipate; a later tick must still reach the quote client, and the worker's
            // catch-all must log exactly one error for the tick that failed.
            await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(3), 4);
            await WaitUntilAsync(() => quoteClient.QuoteCalls > 0, TimeSpan.FromSeconds(10));

            Assert.True(quoteClient.QuoteCalls > 0, "A later tick must still reach the quote client.");
            Assert.Single(log.AtLevel(LogLevel.Error));
        }
        finally
        {
            await subscription.DisposeAsync();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A started-but-not-running worker with one subscriber, one watchlist symbol and a quote
    /// client that counts calls — the setup every cadence test below needs. The caller starts
    /// it and disposes what comes back.
    /// </summary>
    private async Task<SubscribedWorker> NewSubscribedWorkerAsync(
        FakeTimeProvider time, LiveWatchlistOptions options)
    {
        var cache = new LiveQuoteCache(time);
        var db = fixture.CreateContext();
        var watchlist = new WatchlistService(db, time, Options.Create(new LiveWatchlistOptions()));
        await watchlist.AddItemAsync(new CreateWatchlistItemRequest("AAPL"));

        var log = new CapturingLogger<LiveWatchlistPollWorker>();
        var quotes = new CountingQuoteClient();
        var job = new LiveWatchlistPollJob(
            watchlist, new FakeResolver(new Dictionary<string, int> { ["AAPL"] = 1 }), quotes,
            cache, time, Options.Create(new LiveWatchlistOptions()),
            NullLogger<LiveWatchlistPollJob>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(job);
        var provider = services.BuildServiceProvider();

        var worker = new LiveWatchlistPollWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), cache,
            new CountingAuthenticator(), time, Options.Create(options), log);

        // Registered before the first tick, exactly as the SSE endpoint does it: with no
        // subscriber the worker skips every tick and none of these tests would mean anything.
        var subscription = cache.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();
        return new SubscribedWorker(worker, quotes, subscription, db, provider, log);
    }

    private sealed record SubscribedWorker(
        LiveWatchlistPollWorker Worker,
        CountingQuoteClient Quotes,
        IAsyncEnumerator<LiveQuote> Subscription,
        StonkWatchDbContext Db,
        ServiceProvider Provider,
        CapturingLogger<LiveWatchlistPollWorker> Log) : IAsyncDisposable
    {
        /// <summary>
        /// Starts the worker and waits until its loop is parked on an armed timer. Advancing a
        /// <see cref="FakeTimeProvider"/> before that point fires nothing, and the tick is gone
        /// for good — the worker never sees the interval it slept through. The worker logs its
        /// start line after constructing the timer precisely so this wait is deterministic
        /// rather than a sleep long enough to usually win.
        /// </summary>
        public async Task StartAndSettleAsync()
        {
            await Worker.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => Log.AllText.Contains("poll worker started"), TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            await Subscription.DisposeAsync();
            await Provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    // ---------- Market-phase cadence ----------

    [Fact]
    public async Task A_closed_market_polls_once_and_then_waits_out_the_slow_interval()
    {
        var time = new FakeTimeProvider(SundayEvening);
        await using var setup = await NewSubscribedWorkerAsync(
            time, new LiveWatchlistOptions { PollSeconds = 60, ClosedPollSeconds = 300 });

        await setup.StartAndSettleAsync();

        // +60s. The first closed tick polls — the cache is process-memory only, so a restart
        // during a closed stretch has to be able to refill it somehow.
        await AdvanceAndAwaitPollAsync(time, TimeSpan.FromSeconds(60), setup.Quotes);
        Assert.Equal(1, setup.Quotes.QuoteCalls);

        // +120s through +300s: four more ticks, all inside the 300s window since that poll.
        await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(60), 4, settleMs: 100);
        Assert.Equal(1, setup.Quotes.QuoteCalls);

        // +360s: 300s since the last poll, so exactly one more goes out.
        await AdvanceAndAwaitPollAsync(time, TimeSpan.FromSeconds(60), setup.Quotes);
        Assert.Equal(2, setup.Quotes.QuoteCalls);
    }

    [Fact]
    public async Task After_hours_polls_at_the_full_cadence()
    {
        var time = new FakeTimeProvider(TuesdayAfterHours);
        await using var setup = await NewSubscribedWorkerAsync(
            time, new LiveWatchlistOptions { PollSeconds = 3, ClosedPollSeconds = 300 });

        await setup.StartAndSettleAsync();

        // Extended hours are the half of "outside the regular session" where prices still
        // move, and watching them is the whole point of the Ext column. Every tick polls —
        // the slow cadence must not leak into them.
        for (var i = 1; i <= 4; i++)
        {
            await AdvanceAndAwaitPollAsync(time, TimeSpan.FromSeconds(3), setup.Quotes);
            Assert.Equal(i, setup.Quotes.QuoteCalls);
        }
    }

    [Fact]
    public async Task The_full_cadence_resumes_the_moment_the_pre_market_opens()
    {
        var time = new FakeTimeProvider(JustBeforePreMarket);
        await using var setup = await NewSubscribedWorkerAsync(
            time, new LiveWatchlistOptions { PollSeconds = 60, ClosedPollSeconds = 300 });

        await setup.StartAndSettleAsync();

        // 03:57 polls; 03:58 and 03:59 fall inside the window it opened.
        await AdvanceAndAwaitPollAsync(time, TimeSpan.FromSeconds(60), setup.Quotes);
        await AdvanceInStepsAsync(time, TimeSpan.FromSeconds(60), 2, settleMs: 100);
        Assert.Equal(1, setup.Quotes.QuoteCalls);

        // 04:00 and 04:01. Opening the pre-market must not have to wait out the remaining
        // four minutes of a window that started while the market was shut — a user at their
        // desk at 04:00 would otherwise watch dead rows until 04:02.
        await AdvanceAndAwaitPollAsync(time, TimeSpan.FromSeconds(60), setup.Quotes);
        Assert.Equal(2, setup.Quotes.QuoteCalls);
        await AdvanceAndAwaitPollAsync(time, TimeSpan.FromSeconds(60), setup.Quotes);
        Assert.Equal(3, setup.Quotes.QuoteCalls);
    }

    private (WatchlistService Service, StonkWatchDbContext Db) NewWatchlistService()
    {
        var db = fixture.CreateContext();
        var time = new FakeTimeProvider(Start);
        var options = Options.Create(new LiveWatchlistOptions());
        return (new WatchlistService(db, time, options), db);
    }
}

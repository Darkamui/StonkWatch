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
    private static async Task AdvanceInStepsAsync(FakeTimeProvider time, TimeSpan step, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            time.Advance(step);
            await Task.Delay(5);
        }
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

            Assert.True(auth.Calls >= 2, $"Expected at least 2 keepalive refreshes, got {auth.Calls}.");
            // Not called on every skipped tick: 14 ticks, far fewer than 14 refreshes.
            Assert.True(auth.Calls < 14, $"Expected far fewer than 14 refreshes, got {auth.Calls}.");
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

    private (WatchlistService Service, StonkWatchDbContext Db) NewWatchlistService()
    {
        var db = fixture.CreateContext();
        var time = new FakeTimeProvider(Start);
        var options = Options.Create(new LiveWatchlistOptions());
        return (new WatchlistService(db, time, options), db);
    }
}

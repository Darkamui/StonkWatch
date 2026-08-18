using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

public class LiveQuoteCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Session = new(2026, 8, 18);

    private static LiveQuoteCache NewCache() => new(new FakeTimeProvider(Now));

    [Fact]
    public void ApplyTrade_sets_the_last_price()
    {
        var cache = NewCache();

        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.Equal(67.61m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ApplyTrade_discards_a_trade_older_than_the_one_already_stored()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        cache.ApplyTrade(new Trade("ASTS", 60.00m, Now.AddSeconds(-5)));

        // Out-of-order delivery must never rewind a price.
        Assert.Equal(67.61m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ApplySnapshot_does_not_overwrite_a_newer_live_price()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        cache.ApplySnapshot(
            new Quote("ASTS", 60.00m, Now.AddMinutes(-3), Volume: 5_030_000), Session);

        // The slow REST poll must not stomp a fresh tick...
        Assert.Equal(67.61m, cache.Get("ASTS")!.Last);
        // ...but its own fields still land.
        Assert.Equal(5_030_000L, cache.Get("ASTS")!.Volume);
    }

    [Fact]
    public void ApplySnapshot_sets_the_last_price_when_no_live_tick_has_arrived()
    {
        var cache = NewCache();

        cache.ApplySnapshot(new Quote("ASTS", 60.00m, Now), Session);

        Assert.Equal(60.00m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ApplySnapshot_advances_the_last_price_on_an_existing_symbol_when_newer()
    {
        var cache = NewCache();
        cache.ApplySnapshot(new Quote("ASTS", 60.00m, Now.AddMinutes(-10)), Session);

        // Later timestamp than the stored quote, so this snapshot should win on Last too —
        // not just the create path already covered above.
        cache.ApplySnapshot(new Quote("ASTS", 65.00m, Now.AddMinutes(-5)), Session);

        Assert.Equal(65.00m, cache.Get("ASTS")!.Last);
    }

    [Fact]
    public void ApplySnapshot_records_volume_and_extended_fields_on_first_snapshot()
    {
        var cache = NewCache();

        cache.ApplySnapshot(
            new Quote(
                "ASTS", 60.00m, Now,
                Volume: 5_030_000, ExtendedPrice: 61.20m, ExtendedAt: Now.AddMinutes(30)),
            Session);

        var quote = cache.Get("ASTS")!;
        Assert.Equal(5_030_000L, quote.Volume);
        Assert.Equal(Now, quote.VolumeAt);
        Assert.Equal(61.20m, quote.ExtendedPrice);
        Assert.Equal(Now.AddMinutes(30), quote.ExtendedAt);
    }

    [Fact]
    public void ApplySnapshot_advances_volume_and_extended_fields_when_the_snapshot_is_newer()
    {
        var cache = NewCache();
        cache.ApplySnapshot(
            new Quote(
                "ASTS", 60.00m, Now,
                Volume: 5_000_000, ExtendedPrice: 61.00m, ExtendedAt: Now.AddMinutes(30)),
            Session);

        var later = Now.AddMinutes(10);
        cache.ApplySnapshot(
            new Quote(
                "ASTS", 62.00m, later,
                Volume: 5_500_000, ExtendedPrice: 62.50m, ExtendedAt: Now.AddMinutes(45)),
            Session);

        var quote = cache.Get("ASTS")!;
        Assert.Equal(5_500_000L, quote.Volume);
        Assert.Equal(later, quote.VolumeAt);
        Assert.Equal(62.50m, quote.ExtendedPrice);
        Assert.Equal(Now.AddMinutes(45), quote.ExtendedAt);
    }

    [Fact]
    public void ApplySnapshot_does_not_rewind_volume_from_a_stale_snapshot()
    {
        var cache = NewCache();
        cache.ApplySnapshot(new Quote("ASTS", 60.00m, Now, Volume: 5_500_000), Session);

        // An older poll response (a retry, or one overtaken by the next cycle) arrives after
        // a fresher one. Intraday volume only ever climbs, so it must not go backwards.
        cache.ApplySnapshot(
            new Quote("ASTS", 59.00m, Now.AddMinutes(-1), Volume: 5_000_000), Session);

        var quote = cache.Get("ASTS")!;
        Assert.Equal(5_500_000L, quote.Volume);
        Assert.Equal(Now, quote.VolumeAt);
    }

    [Fact]
    public void ApplySnapshot_ignores_an_extended_price_reported_without_its_own_timestamp()
    {
        var cache = NewCache();
        cache.ApplySnapshot(
            new Quote("ASTS", 60.00m, Now, ExtendedPrice: 61.00m, ExtendedAt: Now.AddMinutes(30)),
            Session);

        // TwelveDataQuoteProvider parses ExtendedPrice and ExtendedAt independently, so a
        // payload can produce one without the other. Taking the price alone would stamp a
        // fresh price with a stale timestamp — a wrong claim about when it happened.
        cache.ApplySnapshot(
            new Quote("ASTS", 63.00m, Now.AddMinutes(10), ExtendedPrice: 65.20m, ExtendedAt: null),
            Session);

        var quote = cache.Get("ASTS")!;
        Assert.Equal(61.00m, quote.ExtendedPrice);
        Assert.Equal(Now.AddMinutes(30), quote.ExtendedAt);
    }

    [Fact]
    public void ChangePercent_is_computed_from_last_and_previous_close()
    {
        var cache = NewCache();
        cache.ApplySnapshot(new Quote("ASTS", 60m, Now, PreviousClose: 50m), Session);

        Assert.Equal(20m, cache.Get("ASTS")!.ChangePercent);
    }

    [Fact]
    public void ChangePercent_is_null_without_a_previous_close()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.Null(cache.Get("ASTS")!.ChangePercent);
    }

    [Fact]
    public void ChangePercent_is_null_when_previous_close_is_zero()
    {
        var cache = NewCache();
        cache.ApplySnapshot(new Quote("ASTS", 60m, Now, PreviousClose: 0m), Session);

        Assert.Null(cache.Get("ASTS")!.ChangePercent);
    }

    [Fact]
    public void SymbolsNeedingPreviousClose_reports_symbols_stamped_with_an_earlier_session()
    {
        var cache = NewCache();
        cache.ApplySnapshot(
            new Quote("ASTS", 60m, Now, PreviousClose: 50m), new DateOnly(2026, 8, 17));
        cache.ApplySnapshot(
            new Quote("SPCE", 3m, Now, PreviousClose: 3.1m), Session);

        var stale = cache.SymbolsNeedingPreviousClose(["ASTS", "SPCE", "LLY"], Session);

        // ASTS is from yesterday's session; LLY has never been seen. Both need fetching.
        Assert.Equal(["ASTS", "LLY"], stale.OrderBy(s => s));
    }

    [Fact]
    public void Snapshot_returns_every_applied_quote()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));
        cache.ApplyTrade(new Trade("SPCE", 3.10m, Now));

        var snapshot = cache.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, q => q.Symbol == "ASTS" && q.Last == 67.61m);
        Assert.Contains(snapshot, q => q.Symbol == "SPCE" && q.Last == 3.10m);
    }

    [Fact]
    public void Forget_drops_the_symbol()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        cache.Forget("ASTS");

        Assert.Null(cache.Get("ASTS"));
    }

    [Fact]
    public void Symbols_are_matched_case_insensitively()
    {
        var cache = NewCache();
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.NotNull(cache.Get("asts"));
    }

    [Fact]
    public async Task SubscribeAsync_receives_updates_applied_after_subscribing()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(67.61m, enumerator.Current.Last);
    }

    [Fact]
    public async Task SubscribeAsync_does_not_publish_a_discarded_trade()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        cache.ApplyTrade(new Trade("ASTS", 60.00m, Now.AddSeconds(-5)));  // stale, ignored
        cache.ApplyTrade(new Trade("ASTS", 68.00m, Now.AddSeconds(1)));   // accepted

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(68.00m, enumerator.Current.Last);
    }

    [Fact]
    public async Task SubscribeAsync_fans_a_trade_out_to_every_subscriber()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var first = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var second = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        Assert.True(await first.MoveNextAsync());
        Assert.Equal(67.61m, first.Current.Last);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(67.61m, second.Current.Last);
    }

    [Fact]
    public async Task Unsubscribing_removes_the_subscriber()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.Equal(1, cache.SubscriberCount);

        // Get the enumerator into a "suspended mid-iteration" state (rather than disposing
        // one that never ran) so DisposeAsync deterministically unwinds through the
        // iterator's try/finally instead of being a no-op on a never-started state machine.
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();

        Assert.Equal(0, cache.SubscriberCount);
    }

    [Fact]
    public async Task Disposing_a_subscription_twice_does_not_throw()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));
        await enumerator.MoveNextAsync();

        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        Assert.Equal(0, cache.SubscriberCount);
    }

    [Fact]
    public async Task ApplyTrade_and_ApplySnapshot_interleaved_from_two_threads_agree_with_Get()
    {
        // Regression for the race where install (atomic) and publish (not part of the same
        // atomic step) could happen in opposite order between two ingest threads: thread A
        // installs, then is pre-empted before publishing; thread B installs a newer value and
        // publishes it; thread A resumes and publishes its now-superseded value last. A
        // subscriber then observes LastAt go backwards — a strictly newer write, once
        // installed, must never be followed by an older one on the wire.
        //
        // Two real OS threads, started together off a Barrier (no Task.Delay anywhere), hammer
        // the same symbol via ApplyTrade and ApplySnapshot with strictly increasing timestamps.
        // Several hundred extra, otherwise-unused subscribers are registered first: Publish
        // fans out to every subscriber in a loop, so with more of them that loop takes
        // measurably longer, widening the gap between "install" and "publish" that the race
        // needs to land in. A dedicated reader thread drains the subscription concurrently
        // with the race (not after it finishes) — the channel is bounded and drops its oldest
        // entry once full, so a reader that only starts once both racers have joined would see
        // nothing but the last few hundred frames of a run this size, almost certainly missing
        // the exact pair that would reveal a reordering. Draining live keeps the backlog small
        // enough that a violation actually survives to be observed. This combination reproduces
        // the bug on every run against the pre-fix implementation (verified locally: 8/8), not
        // on a lucky minority of them.
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        for (var i = 0; i < 500; i++)
        {
            cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        }

        const int iterations = 20_000;
        using var barrier = new Barrier(2);

        var tradeThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 1; i <= iterations; i++)
            {
                cache.ApplyTrade(new Trade("ASTS", 100m + i, Now.AddMilliseconds(i)));
            }
        });
        var snapshotThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 1; i <= iterations; i++)
            {
                cache.ApplySnapshot(
                    new Quote("ASTS", 200m + i, Now.AddMilliseconds(i), Volume: i), Session);
            }
        });

        // A sentinel trade, applied only after both racers have joined, deterministically
        // marks the end of the race on the wire: the reader stops as soon as it sees it, so
        // nothing timing-dependent decides when the assertion window closes.
        const decimal sentinel = -1m;
        string? violation = null;
        var reader = new Thread(() =>
        {
            DateTimeOffset? previous = null;
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var quote = enumerator.Current;
                if (quote.Last == sentinel)
                {
                    return;
                }

                if (quote.LastAt is { } lastAt)
                {
                    if (previous is { } prev && lastAt < prev)
                    {
                        violation ??= $"LastAt went backwards: {lastAt:O} after {prev:O}";
                    }

                    previous = lastAt;
                }
            }
        });

        reader.Start();
        tradeThread.Start();
        snapshotThread.Start();
        tradeThread.Join();
        snapshotThread.Join();
        cache.ApplyTrade(new Trade("ASTS", sentinel, Now.AddDays(1)));
        reader.Join();

        Assert.Null(violation);
    }

    // Fix A: SubscribeAsync(subscribeToken).GetAsyncEnumerator(enumerationToken) must link
    // both tokens, not pick one. Before this fix, GetAsyncEnumerator did
    // `cancellationToken == default ? subscribeToken : cancellationToken` -- since
    // enumerationCts.Token is non-default here, that expression discards subscribeToken
    // entirely, so cancelling it would never end the enumeration and MoveNextAsync would
    // hang. Verified this fails (times out against the 5s guard, not a true hang) against
    // the token-picking implementation.
    [Fact]
    public async Task Cancelling_the_subscribe_token_ends_enumeration_started_under_a_different_token()
    {
        var cache = NewCache();
        using var subscribeCts = new CancellationTokenSource();
        using var enumerationCts = new CancellationTokenSource();

        var enumerator = cache.SubscribeAsync(subscribeCts.Token).GetAsyncEnumerator(enumerationCts.Token);

        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));
        Assert.True(await enumerator.MoveNextAsync());

        subscribeCts.Cancel();

        // A bounded guard against a genuine hang if this regresses -- not synchronization
        // for the behavior under test, which is driven entirely by subscribeCts.Cancel()
        // above.
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var winner = await Task.WhenAny(moveNextTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(moveNextTask, winner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNextTask);
    }

    // Fix B: SubscribeAsync itself must do no registration work -- only GetAsyncEnumerator
    // does. Before Fix 4, SubscribeAsync registered the channel eagerly on the call itself
    // (a single shared channel per call, reused by however many times the result was
    // enumerated), so merely calling SubscribeAsync without ever enumerating it already
    // registered a subscriber. Verified this fails (SubscriberCount == 1, not 0) against
    // that implementation.
    [Fact]
    public void Calling_SubscribeAsync_without_enumerating_registers_no_subscriber()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = cache.SubscribeAsync(cts.Token);

        Assert.Equal(0, cache.SubscriberCount);
    }

    // Fix B (second half): two GetAsyncEnumerator calls on one returned IAsyncEnumerable
    // must register two independent subscribers, each with its own channel, both of which
    // receive the same trade. Before Fix 4, the single channel created inside SubscribeAsync
    // was shared by every enumeration of the result, so this registered one subscriber, not
    // two, and having two concurrent readers on a SingleReader = true channel meant at most
    // one of them could ever actually receive a given trade. Verified this fails
    // (SubscriberCount == 1, and the second reader never receives the trade within the 5s
    // guard) against that implementation.
    [Fact]
    public async Task Enumerating_the_same_subscription_twice_registers_two_subscribers_that_both_receive_a_trade()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscription = cache.SubscribeAsync(cts.Token);
        var first = subscription.GetAsyncEnumerator(cts.Token);
        var second = subscription.GetAsyncEnumerator(cts.Token);

        Assert.Equal(2, cache.SubscriberCount);

        cache.ApplyTrade(new Trade("ASTS", 67.61m, Now));

        var firstMoveNext = first.MoveNextAsync().AsTask();
        var secondMoveNext = second.MoveNextAsync().AsTask();
        await Task.WhenAll(firstMoveNext, secondMoveNext);

        Assert.True(await firstMoveNext);
        Assert.True(await secondMoveNext);
        Assert.Equal(67.61m, first.Current.Last);
        Assert.Equal(67.61m, second.Current.Last);
    }

    // Fix C: disposing a subscription that never enumerated (no MoveNextAsync at all) must
    // still remove it. Disposing an async-iterator's state machine before it has started is
    // a documented no-op -- the iterator's own `finally`, where cleanup normally happens,
    // never runs -- so before this fix the subscriber stayed registered forever. Verified
    // this fails (SubscriberCount == 1 after DisposeAsync, not 0) against the
    // implementation that relied solely on the inner iterator's `finally`.
    [Fact]
    public async Task Disposing_a_subscription_that_never_enumerated_removes_the_subscriber()
    {
        var cache = NewCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enumerator = cache.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.Equal(1, cache.SubscriberCount);

        await enumerator.DisposeAsync();

        Assert.Equal(0, cache.SubscriberCount);
    }
}

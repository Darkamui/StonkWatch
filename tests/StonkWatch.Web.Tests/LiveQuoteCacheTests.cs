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
}

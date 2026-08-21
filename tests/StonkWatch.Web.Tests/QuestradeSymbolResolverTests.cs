using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Tests;

/// <summary>
/// Questrade quotes are keyed by a numeric symbolId, not by ticker, so every poll needs a
/// ticker to id map. These tests pin the parts most likely to silently misbehave: preferring
/// an exact match over a prefix neighbour, scoping to US venues so a same-ticker TSX listing
/// can't supply Canadian prices, and negative-caching a ticker that never resolves so a
/// delisted symbol doesn't re-search every poll forever.
/// </summary>
public class QuestradeSymbolResolverTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);

    private static readonly QuestradeSession Session =
        new("access-token-abc", "https://api01.iq.questrade.com/", DateTimeOffset.MaxValue);

    // ---- test doubles -------------------------------------------------------------------

    private sealed class FixedAuthenticator(QuestradeSession session) : IQuestradeAuthenticator
    {
        public int GetSessionCalls;

        public Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetSessionCalls);
            return Task.FromResult(session);
        }

        public void Invalidate()
        {
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Numbers each request (1-based) so a handler can answer the Nth call differently
    /// — needed to script a 401-then-success sequence for the invalidate-and-retry tests.</summary>
    private sealed class SequencedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private int _count;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            Requests.Add(request);
            return Task.FromResult(respond(n, request));
        }
    }

    private sealed class CountingAuthenticator(QuestradeSession session) : IQuestradeAuthenticator
    {
        private int _invalidateCalls;

        public int InvalidateCalls => Volatile.Read(ref _invalidateCalls);

        public Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default) =>
            Task.FromResult(session);

        public void Invalidate() => Interlocked.Increment(ref _invalidateCalls);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string SearchResponse(params (string Symbol, string Exchange, int Id)[] entries)
    {
        var items = string.Join(",", entries.Select(e =>
            $$"""{"symbol":"{{e.Symbol}}","listingExchange":"{{e.Exchange}}","symbolId":{{e.Id}}}"""));
        return $$"""{"symbols":[{{items}}]}""";
    }

    private static QuestradeSymbolResolver NewResolver(
        HttpMessageHandler handler, TimeProvider? time = null, IQuestradeAuthenticator? authenticator = null) =>
        new(new HttpClient(handler), authenticator ?? new FixedAuthenticator(Session), time ?? new FakeTimeProvider(Start),
            NullLogger<QuestradeSymbolResolver>.Instance);

    // ---- tests --------------------------------------------------------------------------

    [Fact]
    public async Task An_exact_ticker_match_is_preferred_over_a_prefix_neighbour()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse(
            ("AAP", "NYSE", 1), ("AAPL", "NASDAQ", 2))));
        var resolver = NewResolver(handler);

        var result = await resolver.ResolveAsync(["AAPL"]);

        Assert.Equal(2, result["AAPL"]);
    }

    [Fact]
    public async Task A_non_US_listing_with_the_same_ticker_is_ignored()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse(
            ("SHOP", "TSX", 1), ("SHOP", "NYSE", 2))));
        var resolver = NewResolver(handler);

        var result = await resolver.ResolveAsync(["SHOP"]);

        Assert.Equal(2, result["SHOP"]);
    }

    [Fact]
    public async Task A_resolved_id_is_not_looked_up_twice()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse(("AAPL", "NASDAQ", 2))));
        var resolver = NewResolver(handler);

        await resolver.ResolveAsync(["AAPL"]);
        await resolver.ResolveAsync(["AAPL"]);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_unresolvable_ticker_is_omitted_and_not_retried_immediately()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse()));
        var time = new FakeTimeProvider(Start);
        var resolver = NewResolver(handler, time);

        var first = await resolver.ResolveAsync(["ZZZZ"]);
        Assert.DoesNotContain("ZZZZ", first.Keys);
        Assert.Single(handler.Requests);

        // Still inside the 30-minute negative-cache window: no second request.
        time.Advance(TimeSpan.FromMinutes(29));
        var second = await resolver.ResolveAsync(["ZZZZ"]);
        Assert.DoesNotContain("ZZZZ", second.Keys);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_ticker_is_retried_once_the_negative_cache_window_elapses()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse()));
        var time = new FakeTimeProvider(Start);
        var resolver = NewResolver(handler, time);

        await resolver.ResolveAsync(["ZZZZ"]);
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromMinutes(31));
        await resolver.ResolveAsync(["ZZZZ"]);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task An_exact_match_on_a_non_US_venue_is_still_rejected_when_no_US_venue_exists()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse(("SHOP", "TSX", 1))));
        var resolver = NewResolver(handler);

        var result = await resolver.ResolveAsync(["SHOP"]);

        Assert.DoesNotContain("SHOP", result.Keys);
    }

    [Fact]
    public async Task A_ticker_that_does_not_resolve_does_not_stop_others_in_the_same_batch()
    {
        var handler = new RecordingHandler(request =>
        {
            var query = request.RequestUri!.Query;
            return query.Contains("AAPL", StringComparison.Ordinal)
                ? Respond(SearchResponse(("AAPL", "NASDAQ", 2)))
                : Respond(SearchResponse());
        });
        var resolver = NewResolver(handler);

        var result = await resolver.ResolveAsync(["ZZZZ", "AAPL"]);

        Assert.DoesNotContain("ZZZZ", result.Keys);
        Assert.Equal(2, result["AAPL"]);
    }

    [Fact]
    public async Task A_ticker_is_normalized_before_it_is_looked_up_or_cached()
    {
        var handler = new RecordingHandler(_ => Respond(SearchResponse(("AAPL", "NASDAQ", 2))));
        var resolver = NewResolver(handler);

        var result = await resolver.ResolveAsync([" aapl "]);
        Assert.Equal(2, result["AAPL"]);
        Assert.Contains("prefix=AAPL", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);

        // A second call with different surrounding whitespace/case must still hit the
        // positive cache under the normalized key, not send a new request.
        await resolver.ResolveAsync(["Aapl"]);
        Assert.Single(handler.Requests);
    }

    // ---------- M-1 fix: a transient HTTP failure must not poison the negative cache ---------

    [Fact]
    public async Task A_transient_search_failure_is_not_negative_cached()
    {
        // First request 401s (a stale access token, not "this ticker doesn't exist"); the
        // retry, after Invalidate(), gets a valid payload. The old bug conflated the two and
        // blacklisted the ticker for 30 minutes; the fix must resolve it in this same call by
        // retrying once, exactly like the quote client already does.
        var handler = new SequencedHandler((n, _) => n == 1
            ? Respond("", HttpStatusCode.Unauthorized)
            : Respond(SearchResponse(("AAPL", "NASDAQ", 2))));
        var auth = new CountingAuthenticator(Session);
        var resolver = NewResolver(handler, authenticator: auth);

        var result = await resolver.ResolveAsync(["AAPL"]);

        Assert.Equal(2, result["AAPL"]);
        Assert.Equal(1, auth.InvalidateCalls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_second_consecutive_401_leaves_the_ticker_unresolved_but_not_negative_cached()
    {
        // A repeated 401 is still a transient failure, not "no such ticker" — it must not be
        // written into the 30-minute negative cache. The next tick (three seconds later in
        // production) gets a fresh chance rather than waiting out a half-hour blackout.
        var handler = new SequencedHandler((_, _) => Respond("", HttpStatusCode.Unauthorized));
        var auth = new CountingAuthenticator(Session);
        var resolver = NewResolver(handler, authenticator: auth);

        var first = await resolver.ResolveAsync(["AAPL"]);
        Assert.DoesNotContain("AAPL", first.Keys);
        Assert.Equal(2, handler.Requests.Count); // one attempt, one retry — bounded, not a loop

        // A second call immediately after must try the network again, not silently answer from
        // a negative cache it should never have been written to.
        var second = await resolver.ResolveAsync(["AAPL"]);
        Assert.DoesNotContain("AAPL", second.Keys);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task A_mid_batch_401_does_not_blacklist_every_remaining_ticker()
    {
        // The old bug fetched one session before the whole lookup loop, so a token that went
        // stale partway through a cold-start batch 401'd (and negative-cached) every ticker
        // still to come. Each lookup must get its own retry-protected session.
        var handler = new SequencedHandler((n, request) =>
        {
            // AAPL 401s once, then succeeds on retry; MSFT must not be swept up in that.
            if (request.RequestUri!.Query.Contains("AAPL", StringComparison.Ordinal) && n <= 2)
            {
                return n == 1
                    ? Respond("", HttpStatusCode.Unauthorized)
                    : Respond(SearchResponse(("AAPL", "NASDAQ", 2)));
            }

            return Respond(SearchResponse(("MSFT", "NASDAQ", 3)));
        });
        var auth = new CountingAuthenticator(Session);
        var resolver = NewResolver(handler, authenticator: auth);

        var result = await resolver.ResolveAsync(["AAPL", "MSFT"]);

        Assert.Equal(2, result["AAPL"]);
        Assert.Equal(3, result["MSFT"]);
    }
}

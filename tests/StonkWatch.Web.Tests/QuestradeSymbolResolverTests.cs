using System.Net;
using Microsoft.Extensions.Logging;
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

    /// <summary>Numbers each request (1-based) like <see cref="SequencedHandler"/>, but throws a
    /// transport-level <see cref="HttpRequestException"/> (StatusCode null — no response was ever
    /// received, unlike a 401) on one chosen call instead of returning a response for it.</summary>
    private sealed class TransportFailureOnNthHandler(
        int failOnCall, Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            Requests.Add(request);
            if (n == failOnCall)
            {
                throw new HttpRequestException("Connection reset by peer.");
            }

            return Task.FromResult(respond(n, request));
        }
    }

    private sealed class CountingAuthenticator(QuestradeSession session) : IQuestradeAuthenticator
    {
        private int _invalidateCalls;
        private int _sessionCalls;

        public int InvalidateCalls => Volatile.Read(ref _invalidateCalls);
        public int SessionCalls => Volatile.Read(ref _sessionCalls);

        public Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _sessionCalls);
            return Task.FromResult(session);
        }

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
        HttpMessageHandler handler, TimeProvider? time = null, IQuestradeAuthenticator? authenticator = null,
        ILogger<QuestradeSymbolResolver>? logger = null) =>
        new(new HttpClient(handler), authenticator ?? new FixedAuthenticator(Session), time ?? new FakeTimeProvider(Start),
            logger ?? NullLogger<QuestradeSymbolResolver>.Instance);

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
    public async Task Each_ticker_gets_its_own_session_fetch_so_one_401_does_not_starve_the_rest_of_the_batch()
    {
        // The old bug fetched one session before the whole lookup loop, so a token that went
        // stale partway through a cold-start batch 401'd (and negative-cached) every ticker
        // still to come. Both tickers resolving isn't proof of that by itself — this test
        // (previously named A_mid_batch_401_does_not_blacklist_every_remaining_ticker) only
        // ever asserted the outcome, never the mechanism its name claimed. Assert the session
        // fetch count directly: a batch-wide shared session (the old bug) would fetch exactly
        // once for both tickers; per-lookup sessions fetch once per attempt.
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
        // 2 session fetches for AAPL's own invalidate-and-retry (before and after Invalidate),
        // 1 more for MSFT's independent lookup.
        Assert.Equal(3, auth.SessionCalls);
    }

    // ---------- fix round 2: N-1, a per-batch circuit breaker on a persistent 401 ----------

    [Fact]
    public async Task A_persistent_401_abandons_the_rest_of_the_batch_instead_of_retrying_every_ticker()
    {
        // Without a circuit breaker, a 50-ticker batch against a token Questrade keeps
        // rejecting retries invalidate-and-retry-once independently for every ticker: 100
        // requests, 100 session fetches, 50 refresh-token rotations, every 3 seconds, forever.
        // The fix: the first persistent-401 TransientFailure ends the batch.
        var handler = new SequencedHandler((_, _) => Respond("", HttpStatusCode.Unauthorized));
        var auth = new CountingAuthenticator(Session);
        var resolver = NewResolver(handler, authenticator: auth);

        var tickers = Enumerable.Range(0, 50).Select(i => $"T{i:D2}").ToList();
        var result = await resolver.ResolveAsync(tickers);

        Assert.Empty(result);
        // Bounded: one ticker's invalidate-and-retry-once, then the batch stops. Not the
        // 100/100/50 shape the unbounded per-ticker retry produced.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, auth.SessionCalls);
        Assert.Equal(1, auth.InvalidateCalls);
    }

    // ---------- fix round 2: gap 1, a non-401 failure must not be read as "not found" ----------

    [Fact]
    public async Task A_500_is_not_negative_cached_either()
    {
        // Same class of bug M-1 was, on a different status code: a transient failure read as
        // evidence of absence. A 500 doesn't take the invalidate-and-retry path (only 401
        // does), so this pins the other branch — QuestradeHttp.SendWithRetryAsync returning
        // null for any other non-success status must still map to TransientFailure, not
        // NotFound.
        var handler = new RecordingHandler(_ => Respond("", HttpStatusCode.InternalServerError));
        var time = new FakeTimeProvider(Start);
        var resolver = NewResolver(handler, time);

        var first = await resolver.ResolveAsync(["AAPL"]);
        Assert.DoesNotContain("AAPL", first.Keys);
        Assert.Single(handler.Requests);

        // If this had been negative-cached, a second call one minute later (well inside the
        // 30-minute window) would still be silently skipped instead of hitting the network.
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await resolver.ResolveAsync(["AAPL"]);
        Assert.DoesNotContain("AAPL", second.Keys);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---------- fix round 2: gap 3, the new LogWarning needs the same leak guard as I-3 -------

    [Fact]
    public async Task The_persistent_401_warning_never_contains_the_access_token()
    {
        var secret = "SECRET-ACCESS-DO-NOT-LEAK-4f2a";
        var session = new QuestradeSession(secret, "https://api01.iq.questrade.com/", DateTimeOffset.MaxValue);
        var handler = new SequencedHandler((_, _) => Respond("", HttpStatusCode.Unauthorized));
        var auth = new CountingAuthenticator(session);
        var log = new CapturingLogger<QuestradeSymbolResolver>();
        var resolver = NewResolver(handler, authenticator: auth, logger: log);

        await resolver.ResolveAsync(["AAPL", "MSFT"]);

        Assert.NotEmpty(log.AtLevel(LogLevel.Warning));
        Assert.DoesNotContain(secret, log.AllText, StringComparison.Ordinal);
    }

    // ---------- fix round 3: N-2, only a persistent 401 may trip the circuit breaker ----------

    [Fact]
    public async Task A_transport_failure_costs_only_that_ticker_and_the_batch_continues()
    {
        // The round-2 fix caught *any* HttpRequestException as a persistent-401 signal, so a
        // one-off socket reset (StatusCode null, not Unauthorized — no response was ever
        // received) tripped the batch-wide circuit breaker meant only for a stuck token: the
        // third ticker's transport failure would have abandoned the fourth and fifth too, even
        // though nothing was wrong with the token. Widening the catch back to
        // `catch (HttpRequestException)` — dropping the `when (ex.StatusCode == Unauthorized)`
        // guard — reproduces exactly that and turns this test red.
        var handler = new TransportFailureOnNthHandler(failOnCall: 3, respond: (n, request) =>
        {
            var ticker = request.RequestUri!.Query.Contains("T1", StringComparison.Ordinal) ? "T1"
                : request.RequestUri!.Query.Contains("T2", StringComparison.Ordinal) ? "T2"
                : request.RequestUri!.Query.Contains("T4", StringComparison.Ordinal) ? "T4"
                : "T5";
            return Respond(SearchResponse((ticker, "NASDAQ", n)));
        });
        var auth = new CountingAuthenticator(Session);
        var resolver = NewResolver(handler, authenticator: auth);

        var result = await resolver.ResolveAsync(["T1", "T2", "T3", "T4", "T5"]);

        // T3's own lookup throws and is left unresolved for this tick, but T4 and T5 — ordered
        // after it — still get looked up and resolve: the breaker did not trip.
        Assert.Equal(1, result["T1"]);
        Assert.Equal(2, result["T2"]);
        Assert.DoesNotContain("T3", result.Keys);
        Assert.Equal(4, result["T4"]);
        Assert.Equal(5, result["T5"]);
        Assert.Equal(5, handler.Requests.Count);
        // A transport failure never reaches a response, so it can't be a 401 and must never
        // invalidate a perfectly good session.
        Assert.Equal(0, auth.InvalidateCalls);
    }
}

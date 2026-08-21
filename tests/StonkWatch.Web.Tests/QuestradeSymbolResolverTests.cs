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
}

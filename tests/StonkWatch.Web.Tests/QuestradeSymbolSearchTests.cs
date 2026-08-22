using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Tests;

/// <summary>
/// The sidebar's add box runs on this. What matters is that it offers only symbols the poller
/// can actually price, that an upstream failure never reads as "no such symbol", and that it
/// does not turn keystrokes into unbounded growth in the resolver's process-lifetime cache.
/// </summary>
public class QuestradeSymbolSearchTests
{
    private static readonly QuestradeSession Session =
        new("access-token-abc", "https://api01.iq.questrade.com/", DateTimeOffset.MaxValue);

    private sealed class FixedAuthenticator : IQuestradeAuthenticator
    {
        public Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default) =>
            Task.FromResult(Session);

        public void Invalidate()
        {
        }
    }

    /// <summary>Records what the search chose to prime, and nothing else.</summary>
    private sealed class RecordingResolver : IQuestradeSymbolResolver
    {
        public List<(string Ticker, int SymbolId)> Primed { get; } = [];

        public Task<IReadOnlyDictionary<string, int>> ResolveAsync(
            IReadOnlyCollection<string> tickers, CancellationToken ct = default) =>
            throw new NotSupportedException("Search never resolves.");

        public void Prime(string ticker, int symbolId) => Primed.Add((ticker, symbolId));
    }

    private static (QuestradeSymbolSearch Search, RecordingResolver Resolver, StubHttpMessageHandler Handler)
        NewSearch(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = StubHttpMessageHandler.Json(body, status);
        var resolver = new RecordingResolver();
        var search = new QuestradeSymbolSearch(
            new HttpClient(handler), new FixedAuthenticator(), resolver,
            NullLogger<QuestradeSymbolSearch>.Instance);
        return (search, resolver, handler);
    }

    private const string NvidiaBody =
        """
        {"symbols":[
          {"symbol":"NVDA","symbolId":8049,"description":"NVIDIA CORP",
           "securityType":"Stock","listingExchange":"NASDAQ"},
          {"symbol":"NVDA.TO","symbolId":9101,"description":"NVIDIA CDR",
           "securityType":"Stock","listingExchange":"TSX"}
        ]}
        """;

    [Fact]
    public async Task Only_us_listings_are_offered()
    {
        var (search, _, _) = NewSearch(NvidiaBody);

        var results = await search.SearchAsync("NVDA", 10);

        // The TSX line is dropped on purpose. Offering it would let the user add a symbol
        // QuestradeSymbolResolver then refuses to resolve — the row would sit at an em dash
        // forever with nothing anywhere explaining why.
        var only = Assert.Single(results);
        Assert.Equal("NVDA", only.Symbol);
        Assert.Equal("NASDAQ", only.Exchange);
        Assert.Equal(8049, only.SymbolId);
        Assert.Equal("NVIDIA CORP", only.Description);
    }

    [Fact]
    public async Task The_limit_is_honoured()
    {
        var symbols = string.Join(",", Enumerable.Range(1, 25).Select(i =>
            $$"""{"symbol":"AA{{i}}","symbolId":{{i}},"description":"Co {{i}}","listingExchange":"NYSE"}"""));
        var (search, _, _) = NewSearch($$"""{"symbols":[{{symbols}}]}""");

        var results = await search.SearchAsync("AA", 10);

        Assert.Equal(10, results.Count);
    }

    [Fact]
    public async Task A_search_that_matched_nothing_returns_an_empty_list()
    {
        var (search, _, _) = NewSearch("""{"symbols":[]}""");

        Assert.Empty(await search.SearchAsync("ZZZZZ", 10));
    }

    [Fact]
    public async Task A_body_without_a_symbols_array_returns_an_empty_list()
    {
        var (search, _, _) = NewSearch("""{"code":0}""");

        Assert.Empty(await search.SearchAsync("NVDA", 10));
    }

    [Fact]
    public async Task An_upstream_failure_throws_rather_than_reporting_no_matches()
    {
        var (search, _, _) = NewSearch("""{"message":"boom"}""", HttpStatusCode.InternalServerError);

        // The poller answers a bad tick with "nothing this time" and moves on, which is right
        // for a background loop. Here it would be a lie told to someone's face: a Questrade
        // outage would render as "that symbol does not exist" and send the user off to check
        // a ticker that was correct all along.
        await Assert.ThrowsAsync<HttpRequestException>(() => search.SearchAsync("NVDA", 10));
    }

    [Fact]
    public async Task A_blank_prefix_never_reaches_questrade()
    {
        var (search, _, handler) = NewSearch(NvidiaBody);

        Assert.Empty(await search.SearchAsync("   ", 10));

        // The box sends the field's contents as the user clears it. Forwarding that would ask
        // Questrade to prefix-match on nothing at all.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_prefix_is_escaped_into_the_query_string()
    {
        var (search, _, handler) = NewSearch(NvidiaBody);

        await search.SearchAsync("BRK B", 10);

        // AbsoluteUri, not ToString(): Uri.ToString() unescapes for display, so it would
        // show "prefix=BRK B" and pass whether or not the escaping ever happened.
        var request = Assert.Single(handler.Requests);
        Assert.Contains("prefix=BRK%20B", request.AbsoluteUri);
    }

    [Fact]
    public async Task An_exact_ticker_match_primes_the_resolver()
    {
        var (search, resolver, _) = NewSearch(NvidiaBody);

        await search.SearchAsync("nvda", 10);

        // Priming is what lets a symbol the user just watched Questrade confirm escape a
        // stale negative-cache entry, instead of showing nothing for up to half an hour
        // after being added. Case-insensitive: the box does not force upper case.
        var primed = Assert.Single(resolver.Primed);
        Assert.Equal("NVDA", primed.Ticker);
        Assert.Equal(8049, primed.SymbolId);
    }

    [Fact]
    public async Task Prefix_neighbours_are_never_primed()
    {
        var (search, resolver, _) = NewSearch(NvidiaBody);

        await search.SearchAsync("NV", 10);

        // The resolver's positive cache lives as long as the process. Priming every match
        // would pour a new entry into it for each result on the way to typing a ticker —
        // growth driven by keystrokes, for symbols nobody added.
        Assert.Empty(resolver.Primed);
    }
}

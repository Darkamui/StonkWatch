using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Tests;

/// <summary>
/// Batched Questrade quote and previous-close REST calls. These tests pin the failure paths
/// most likely to silently break the sidebar: a 401 from an expired access token has to
/// invalidate and retry exactly once (not loop forever), any other failure must return an
/// empty result rather than kill the poll loop, and the bearer token must never reach a log
/// line no matter which of those paths runs.
/// </summary>
public class QuestradeQuoteClientTests
{
    // ---- test doubles -------------------------------------------------------------------

    private sealed class RecordingAuthenticator(
        string accessToken, string apiServer = "https://api01.iq.questrade.com/") : IQuestradeAuthenticator
    {
        private int _getSessionCalls;
        private int _invalidateCalls;

        public int GetSessionCalls => Volatile.Read(ref _getSessionCalls);
        public int InvalidateCalls => Volatile.Read(ref _invalidateCalls);

        public Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getSessionCalls);
            return Task.FromResult(new QuestradeSession(accessToken, apiServer, DateTimeOffset.MaxValue));
        }

        public void Invalidate() => Interlocked.Increment(ref _invalidateCalls);
    }

    private sealed class SequencedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private int _count;

        public List<HttpRequestMessage> Requests { get; } = [];
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            Requests.Add(request);
            return Task.FromResult(respond(n, request));
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Num(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string QuotesResponse(
        params (int Id, string Symbol, decimal? Last, decimal? TrHrs, long? Volume)[] entries)
    {
        var items = string.Join(",", entries.Select(e =>
            $$"""
              {"symbolId":{{e.Id}},"symbol":"{{e.Symbol}}","lastTradePrice":{{Num(e.Last)}},
               "lastTradePriceTrHrs":{{Num(e.TrHrs)}},"volume":{{(e.Volume?.ToString() ?? "null")}}}
              """));
        return $$"""{"quotes":[{{items}}]}""";
    }

    private static QuestradeQuoteClient NewClient(
        HttpMessageHandler handler, IQuestradeAuthenticator authenticator, ILogger<QuestradeQuoteClient>? logger = null) =>
        new(new HttpClient(handler), authenticator, logger ?? NullLogger<QuestradeQuoteClient>.Instance);

    // ---- tests --------------------------------------------------------------------------

    [Fact]
    public async Task GetQuotesAsync_sends_one_batched_request_for_the_whole_list()
    {
        var handler = new SequencedHandler((_, _) => Respond(QuotesResponse(
            (1, "AAPL", 150.25m, 150.30m, 1_000_000),
            (2, "MSFT", 300.00m, 301.00m, 500_000))));
        var client = NewClient(handler, new RecordingAuthenticator("token"));

        var result = await client.GetQuotesAsync([1, 2]);

        Assert.Single(handler.Requests);
        Assert.Contains("ids=1,2", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(2, result.Count);
        Assert.Equal("AAPL", result[1].Symbol);
        Assert.Equal(150.30m, result[1].LastTradePriceTrHrs);
        Assert.Equal(1_000_000L, result[1].Volume);
    }

    [Fact]
    public async Task GetPreviousClosesAsync_reads_prevDayClosePrice()
    {
        var handler = new SequencedHandler((_, _) => Respond(
            """{"symbols":[{"symbolId":1,"prevDayClosePrice":148.50}]}"""));
        var client = NewClient(handler, new RecordingAuthenticator("token"));

        var result = await client.GetPreviousClosesAsync([1]);

        Assert.Equal(148.50m, result[1]);
        Assert.Contains("v1/symbols", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_invalidates_the_session_and_retries_once()
    {
        var handler = new SequencedHandler((n, _) => n == 1
            ? Respond("", HttpStatusCode.Unauthorized)
            : Respond(QuotesResponse((1, "AAPL", 150.25m, 150.30m, 1_000_000))));
        var auth = new RecordingAuthenticator("secret-access-token");
        var client = NewClient(handler, auth);

        var result = await client.GetQuotesAsync([1]);

        Assert.Equal(150.30m, result[1].LastTradePriceTrHrs);
        Assert.Equal(1, auth.InvalidateCalls);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task A_second_401_throws()
    {
        var handler = new SequencedHandler((_, _) => Respond("", HttpStatusCode.Unauthorized));
        var auth = new RecordingAuthenticator("secret-access-token");
        var client = NewClient(handler, auth);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetQuotesAsync([1]));

        // Exactly two attempts: the retry is bounded, not infinite.
        Assert.Equal(2, handler.Count);
        Assert.Equal(1, auth.InvalidateCalls);
    }

    [Fact]
    public async Task A_500_returns_an_empty_result_rather_than_throwing()
    {
        var handler = new SequencedHandler((_, _) => Respond("upstream boom", HttpStatusCode.InternalServerError));
        var client = NewClient(handler, new RecordingAuthenticator("secret-access-token"));

        var result = await client.GetQuotesAsync([1]);

        Assert.Empty(result);
    }

    [Fact]
    public async Task The_access_token_never_appears_in_the_logs()
    {
        const string secret = "SECRET-ACCESS-DO-NOT-LEAK-4f2a";
        var log = new CapturingLogger<QuestradeQuoteClient>();

        var handler500 = new SequencedHandler((_, _) => Respond("upstream boom", HttpStatusCode.InternalServerError));
        var client500 = NewClient(handler500, new RecordingAuthenticator(secret), log);
        await client500.GetQuotesAsync([1]);

        var handler401 = new SequencedHandler((_, _) => Respond("", HttpStatusCode.Unauthorized));
        var client401 = NewClient(handler401, new RecordingAuthenticator(secret), log);
        await Assert.ThrowsAsync<HttpRequestException>(() => client401.GetQuotesAsync([1]));

        Assert.DoesNotContain(secret, log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_symbol_ids_do_not_reach_the_network()
    {
        var handler = new SequencedHandler((_, _) => Respond(QuotesResponse()));
        var client = NewClient(handler, new RecordingAuthenticator("token"));

        var result = await client.GetQuotesAsync([]);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }
}

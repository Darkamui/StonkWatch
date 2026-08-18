using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

public class TwelveDataQuoteProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 14, 30, 0, TimeSpan.Zero);

    private static TwelveDataQuoteProvider Build(
        StubHttpMessageHandler handler, int batchSize = 20)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var options = Options.Create(new MarketDataOptions
        {
            ApiKey = "test-key",
            BatchSize = batchSize
        });

        return new TwelveDataQuoteProvider(
            http, options, new FakeTimeProvider(Now),
            NullLogger<TwelveDataQuoteProvider>.Instance);
    }

    // A batch request returns an object keyed by symbol.
    private const string BatchBody = """
        {
          "AAPL": {
            "symbol": "AAPL", "name": "Apple Inc", "exchange": "NASDAQ", "currency": "USD",
            "datetime": "2026-07-31", "timestamp": 1785508200,
            "open": "181.99", "high": "182.76", "low": "180.17", "close": "181.18",
            "volume": "62303236", "is_market_open": true
          },
          "MSFT": {
            "symbol": "MSFT", "name": "Microsoft", "exchange": "NASDAQ", "currency": "USD",
            "datetime": "2026-07-31", "timestamp": 1785508200,
            "open": "405.10", "high": "410.00", "low": "404.00", "close": "409.55",
            "volume": "18220100", "is_market_open": true
          }
        }
        """;

    // A single-symbol request returns the quote object itself, not a keyed map.
    private const string SingleBody = """
        {
          "symbol": "ASTS", "name": "AST SpaceMobile", "exchange": "NASDAQ", "currency": "USD",
          "datetime": "2026-07-31", "timestamp": 1785508200,
          "open": "56.10", "high": "58.20", "low": "55.90", "close": "57.25",
          "volume": "8123400", "is_market_open": true
        }
        """;

    [Fact]
    public async Task Parses_a_batch_response()
    {
        var provider = Build(StubHttpMessageHandler.Json(BatchBody));

        var quotes = await provider.GetQuotesAsync(["AAPL", "MSFT"]);

        Assert.Equal(2, quotes.Count);
        Assert.Equal(181.18m, quotes["AAPL"].Price);
        Assert.Equal(409.55m, quotes["MSFT"].Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785508200), quotes["AAPL"].At);
    }

    [Fact]
    public async Task Parses_a_single_symbol_response()
    {
        var provider = Build(StubHttpMessageHandler.Json(SingleBody));

        var quotes = await provider.GetQuotesAsync(["ASTS"]);

        Assert.Equal(57.25m, Assert.Single(quotes).Value.Price);
    }

    [Fact]
    public async Task Skips_a_symbol_that_errored_inside_a_batch()
    {
        var provider = Build(StubHttpMessageHandler.Json("""
            {
              "AAPL": {
                "symbol": "AAPL", "close": "181.18", "timestamp": 1785508200
              },
              "NOTREAL": {
                "code": 404, "message": "**symbol** not found", "status": "error"
              }
            }
            """));

        var quotes = await provider.GetQuotesAsync(["AAPL", "NOTREAL"]);

        // The good symbol still comes through — one bad ticker must not lose the cycle.
        Assert.Equal(181.18m, Assert.Single(quotes).Value.Price);
        Assert.False(quotes.ContainsKey("NOTREAL"));
    }

    [Fact]
    public async Task Returns_empty_when_the_whole_request_is_rejected()
    {
        // Rate limiting arrives as HTTP 200 with an error body, not a 429.
        var provider = Build(StubHttpMessageHandler.Json("""
            {
              "code": 429,
              "message": "You have run out of API credits for the current minute.",
              "status": "error"
            }
            """));

        Assert.Empty(await provider.GetQuotesAsync(["AAPL", "MSFT"]));
    }

    [Fact]
    public async Task Returns_empty_on_an_http_error()
    {
        var provider = Build(StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError));

        Assert.Empty(await provider.GetQuotesAsync(["AAPL"]));
    }

    [Fact]
    public async Task Chunks_requests_at_the_configured_batch_size()
    {
        var handler = StubHttpMessageHandler.Sequence(BatchBody, SingleBody);
        var provider = Build(handler, batchSize: 2);

        await provider.GetQuotesAsync(["AAPL", "MSFT", "ASTS"]);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("AAPL%2CMSFT", handler.Requests[0].Query);
        Assert.Contains("ASTS", handler.Requests[1].Query);
    }

    [Fact]
    public async Task Normalises_and_deduplicates_symbols_before_requesting()
    {
        var handler = StubHttpMessageHandler.Json(SingleBody);
        var provider = Build(handler);

        await provider.GetQuotesAsync(["  asts ", "ASTS", "asts"]);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("symbol=ASTS&", request.Query);
    }

    [Fact]
    public async Task Sends_no_request_when_there_are_no_symbols()
    {
        var handler = StubHttpMessageHandler.Json(BatchBody);
        var provider = Build(handler);

        Assert.Empty(await provider.GetQuotesAsync([]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Parses_decimals_independently_of_host_locale()
    {
        // The host may format decimals with a comma; the payload always uses a point.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            var provider = Build(StubHttpMessageHandler.Json(SingleBody));
            var quotes = await provider.GetQuotesAsync(["ASTS"]);

            Assert.Equal(57.25m, quotes["ASTS"].Price);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Falls_back_to_the_current_time_when_the_timestamp_is_missing()
    {
        var provider = Build(StubHttpMessageHandler.Json("""
            { "symbol": "ASTS", "close": "57.25" }
            """));

        Assert.Equal(Now, (await provider.GetQuotesAsync(["ASTS"]))["ASTS"].At);
    }

    [Fact]
    public async Task Skips_a_quote_with_an_unparseable_price()
    {
        var provider = Build(StubHttpMessageHandler.Json("""
            { "ASTS": { "symbol": "ASTS", "close": "n/a" } }
            """));

        Assert.Empty(await provider.GetQuotesAsync(["ASTS"]));
    }

    [Fact]
    public async Task Result_lookup_is_case_insensitive()
    {
        var provider = Build(StubHttpMessageHandler.Json(SingleBody));

        var quotes = await provider.GetQuotesAsync(["ASTS"]);

        Assert.True(quotes.ContainsKey("asts"));
    }

    [Fact]
    public async Task GetQuotesAsync_reads_volume_and_previous_close()
    {
        var provider = Build(StubHttpMessageHandler.Json("""
            {"symbol":"ASTS","close":"67.61","previous_close":"71.14",
             "volume":"5030000","timestamp":"1785600000"}
            """));

        var quotes = await provider.GetQuotesAsync(["ASTS"]);

        Assert.Equal(67.61m, quotes["ASTS"].Price);
        Assert.Equal(71.14m, quotes["ASTS"].PreviousClose);
        Assert.Equal(5_030_000L, quotes["ASTS"].Volume);
    }

    [Fact]
    public async Task GetQuotesAsync_tolerates_missing_optional_fields()
    {
        var provider = Build(StubHttpMessageHandler.Json("""
            {"symbol":"ASTS","close":"67.61","timestamp":"1785600000"}
            """));

        var quotes = await provider.GetQuotesAsync(["ASTS"]);

        Assert.Equal(67.61m, quotes["ASTS"].Price);
        Assert.Null(quotes["ASTS"].PreviousClose);
        Assert.Null(quotes["ASTS"].Volume);
    }
}

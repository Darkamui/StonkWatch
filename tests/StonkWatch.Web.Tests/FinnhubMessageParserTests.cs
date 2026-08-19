using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

public class FinnhubMessageParserTests
{
    [Fact]
    public void ParseTrades_reads_a_single_trade()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[{"s":"ASTS","p":67.61,"t":1787059800000,"v":100}]}
            """);

        var trade = Assert.Single(trades);
        Assert.Equal("ASTS", trade.Symbol);
        Assert.Equal(67.61m, trade.Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787059800000), trade.At);
    }

    [Fact]
    public void ParseTrades_reads_every_trade_in_a_batched_message()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[
              {"s":"ASTS","p":67.61,"t":1787059800000,"v":100},
              {"s":"SPCE","p":3.18,"t":1787059801000,"v":50}]}
            """);

        Assert.Equal(2, trades.Count);
        Assert.Equal(["ASTS", "SPCE"], trades.Select(t => t.Symbol));
    }

    [Theory]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"trade"}""")]
    [InlineData("""{"type":"error","msg":"Invalid symbol"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void ParseTrades_returns_empty_for_anything_that_is_not_a_trade(string payload)
    {
        // The read loop must never throw on an unexpected frame; one bad message
        // cannot be allowed to tear down the connection.
        Assert.Empty(FinnhubMessageParser.ParseTrades(payload));
    }

    [Fact]
    public void ParseTrades_skips_a_malformed_entry_but_keeps_the_rest()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[
              {"s":"ASTS","t":1787059800000},
              {"s":"SPCE","p":3.18,"t":1787059801000}]}
            """);

        var trade = Assert.Single(trades);
        Assert.Equal("SPCE", trade.Symbol);
    }

    [Fact]
    public void ParseTrades_uppercases_the_symbol()
    {
        var trades = FinnhubMessageParser.ParseTrades("""
            {"type":"trade","data":[{"s":"asts","p":67.61,"t":1787059800000}]}
            """);

        Assert.Equal("ASTS", trades[0].Symbol);
    }
}

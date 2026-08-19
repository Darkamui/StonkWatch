using System.Text.Json;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// Pure parsing of Finnhub websocket frames, separated from the connection so it can be
/// tested exhaustively without a socket — the same split
/// <see cref="TwelveDataQuoteProvider"/> uses for its REST payloads.
/// </summary>
/// <remarks>
/// A trade frame looks like:
/// <c>{"type":"trade","data":[{"s":"ASTS","p":67.61,"t":1787059800000,"v":100}]}</c>.
/// Note that <c>v</c> is the size of this one trade, not cumulative daily volume — daily
/// volume is not available on this feed at all and comes from the REST snapshot instead.
/// </remarks>
public static class FinnhubMessageParser
{
    public static IReadOnlyList<Trade> ParseTrades(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Never throw out of the read loop: one unparseable frame must not kill the feed.
            return [];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "trade"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var trades = new List<Trade>(data.GetArrayLength());
            foreach (var entry in data.EnumerateArray())
            {
                if (TryParseTrade(entry, out var trade))
                {
                    trades.Add(trade);
                }
            }

            return trades;
        }
    }

    private static bool TryParseTrade(JsonElement element, out Trade trade)
    {
        trade = default!;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("s", out var s)
            || s.GetString() is not { Length: > 0 } symbol
            || !element.TryGetProperty("p", out var p)
            || p.ValueKind != JsonValueKind.Number
            || !element.TryGetProperty("t", out var t)
            || t.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        trade = new Trade(
            symbol.Trim().ToUpperInvariant(),
            p.GetDecimal(),
            DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()));
        return true;
    }
}

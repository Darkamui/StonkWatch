using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// Twelve Data <c>/quote</c> client.
/// </summary>
/// <remarks>
/// The response shape is not consistent, which is most of what this class deals with:
/// one symbol returns a bare quote object, several return an object keyed by symbol, and a
/// failure — for a single symbol, for the whole request, or for one symbol inside a batch —
/// arrives as HTTP 200 with <c>"status": "error"</c> rather than an error status code.
/// </remarks>
public class TwelveDataQuoteProvider(
    HttpClient http,
    IOptions<MarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<TwelveDataQuoteProvider> logger) : IQuoteProvider
{
    private readonly MarketDataOptions _options = options.Value;

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var results = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);

        var normalised = symbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var batch in normalised.Chunk(_options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            await FetchBatchAsync(batch, results, ct);
        }

        return results;
    }

    private async Task FetchBatchAsync(
        string[] batch, Dictionary<string, Quote> results, CancellationToken ct)
    {
        // The API key travels as a query parameter, so this URL must never be logged.
        var url = $"quote?symbol={Uri.EscapeDataString(string.Join(',', batch))}"
                  + $"&apikey={Uri.EscapeDataString(_options.ApiKey)}";

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Quote request for {SymbolCount} symbols failed with {StatusCode}",
                batch.Length, (int)response.StatusCode);
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            logger.LogWarning("Unexpected quote payload for {Symbols}", string.Join(',', batch));
            return;
        }

        // Whole-request failure: bad key, exhausted credits, or a single unknown symbol.
        if (IsErrorPayload(root))
        {
            logger.LogWarning(
                "Quote request rejected for {Symbols}: {Message}",
                string.Join(',', batch), ReadString(root, "message") ?? "no message");
            return;
        }

        // A single-symbol request returns the quote object itself, not a keyed map.
        if (root.TryGetProperty("symbol", out _))
        {
            if (TryParseQuote(root, out var single))
            {
                results[single.Symbol] = single;
            }
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (IsErrorPayload(property.Value))
            {
                logger.LogWarning(
                    "No quote for {Symbol}: {Message}",
                    property.Name, ReadString(property.Value, "message") ?? "no message");
                continue;
            }

            if (TryParseQuote(property.Value, out var quote))
            {
                results[quote.Symbol] = quote;
            }
            else
            {
                logger.LogWarning("Unparseable quote for {Symbol}", property.Name);
            }
        }
    }

    private static bool IsErrorPayload(JsonElement element) =>
        string.Equals(ReadString(element, "status"), "error", StringComparison.OrdinalIgnoreCase);

    private bool TryParseQuote(JsonElement element, out Quote quote)
    {
        quote = default!;

        var symbol = ReadString(element, "symbol");
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        // Numbers arrive as strings. InvariantCulture is required — the host locale may use
        // a comma as the decimal separator, which would silently mis-parse "181.18".
        var raw = ReadString(element, "close") ?? ReadString(element, "price");
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
        {
            return false;
        }

        // Every one of these is optional. A missing or unparseable field must leave the
        // quote usable rather than discard it — the price is what the alert worker needs,
        // and the rest only decorate the live sidebar.
        long? volume = long.TryParse(
            ReadString(element, "volume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : null;

        decimal? previousClose = decimal.TryParse(
            ReadString(element, "previous_close"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pc)
            ? pc : null;

        decimal? extendedPrice = decimal.TryParse(
            ReadString(element, "extended_price"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ep)
            ? ep : null;

        DateTimeOffset? extendedAt = long.TryParse(
            ReadString(element, "extended_timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ex)
            ? DateTimeOffset.FromUnixTimeSeconds(ex) : null;

        quote = new Quote(
            symbol.Trim().ToUpperInvariant(), price, ReadTimestamp(element),
            volume, previousClose, extendedPrice, extendedAt);
        return true;
    }

    private DateTimeOffset ReadTimestamp(JsonElement element)
    {
        var raw = ReadString(element, "timestamp");
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return timeProvider.GetUtcNow();
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }
}

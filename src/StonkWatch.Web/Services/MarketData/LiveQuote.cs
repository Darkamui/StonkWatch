namespace StonkWatch.Web.Services.MarketData;

/// <summary>One executed trade pushed by a streaming provider.</summary>
public record Trade(string Symbol, decimal Price, DateTimeOffset At);

/// <summary>
/// The live view of one symbol. Never persisted. Each field carries its own timestamp
/// because they arrive from different places at different rates: Last is pushed
/// sub-second over a websocket, Volume is polled over REST every few minutes.
/// </summary>
public record LiveQuote(
    string Symbol,
    decimal? Last = null,
    DateTimeOffset? LastAt = null,
    decimal? PreviousClose = null,
    DateOnly? PreviousCloseSession = null,
    long? Volume = null,
    DateTimeOffset? VolumeAt = null,
    decimal? ExtendedPrice = null,
    DateTimeOffset? ExtendedAt = null)
{
    /// <summary>
    /// Null — never zero — when there is no baseline to measure against. A fabricated
    /// "0.00%" reads as "flat today", which is a materially different claim from
    /// "we don't know yet".
    /// </summary>
    public decimal? ChangePercent =>
        Last is { } last && PreviousClose is { } previousClose && previousClose != 0
            ? (last - previousClose) / previousClose * 100m
            : null;
}

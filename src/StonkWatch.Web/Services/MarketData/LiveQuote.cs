namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// One executed trade, for <see cref="LiveQuoteCache.ApplyTrade"/>. Nothing produces one
/// today — Questrade is poll-only, so every quote reaches the cache as a snapshot.
/// </summary>
public record Trade(string Symbol, decimal Price, DateTimeOffset At);

/// <summary>
/// The live view of one symbol. Never persisted. Each field carries its own timestamp
/// because a single poll does not refresh them equally: Questrade returns last price,
/// volume and the extended-hours print together, but any of them can be older than the
/// response that carried it, and each is merged against its own clock.
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

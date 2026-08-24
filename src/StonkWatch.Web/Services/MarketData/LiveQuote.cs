namespace StonkWatch.Web.Services.MarketData;

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
    DateTimeOffset? ExtendedAt = null,
    decimal? RegularClose = null)
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

    /// <summary>
    /// How far the extended-hours print has moved off the regular session, as a percentage —
    /// what a broker's "Ext" column shows. Measured against <see cref="RegularClose"/>, not
    /// <see cref="PreviousClose"/>: after the bell those are different days, and the
    /// interesting number is the move since today's close, not since yesterday's.
    /// </summary>
    /// <remarks>
    /// Pre-market is the degenerate case, and deliberately so. No regular session has happened
    /// yet, so <see cref="RegularClose"/> is the previous day's close and this reads the same
    /// as <see cref="ChangePercent"/>. That is the honest answer rather than a coincidence to
    /// paper over: the pre-market move and the move since the last close are the same move.
    /// </remarks>
    public decimal? ExtendedChangePercent =>
        ExtendedPrice is { } extended && RegularClose is { } regularClose && regularClose != 0
            ? (extended - regularClose) / regularClose * 100m
            : null;
}

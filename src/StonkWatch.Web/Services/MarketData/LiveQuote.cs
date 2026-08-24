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
    DateTimeOffset? ExtendedAt = null)
{
    /// <summary>
    /// The regular session's own move, in percent — the "Chg%" column. Both sides belong to
    /// the session on screen: <see cref="Last"/> is that session's price (live during it, its
    /// closing print afterwards) and <see cref="PreviousClose"/> is the close of the session
    /// before it. Extended-hours trading never enters this number; it is
    /// <see cref="ExtendedChangePercent"/>'s job.
    /// </summary>
    /// <remarks>
    /// Null — never zero — when there is no baseline to measure against. A fabricated
    /// "0.00%" reads as "flat today", which is a materially different claim from
    /// "we don't know yet".
    /// </remarks>
    public decimal? ChangePercent =>
        Last is { } last && PreviousClose is { } previousClose && previousClose != 0
            ? (last - previousClose) / previousClose * 100m
            : null;

    /// <summary>
    /// How far the extended-hours print has moved off the regular session, as a percentage —
    /// what a broker's "Ext" column shows. The baseline is <see cref="Last"/>, not
    /// <see cref="PreviousClose"/>, and outside the regular session that is exactly the right
    /// one: <see cref="Last"/> is then the displayed session's closing print, so this reads as
    /// the move since that close — after the bell, today's; before it, yesterday's.
    /// </summary>
    /// <remarks>
    /// Set only outside regular hours. During the session the extended price is the live price,
    /// so a percentage here would either be a duplicate of <see cref="ChangePercent"/> or a
    /// flat zero; the producer leaves <see cref="ExtendedPrice"/> null instead.
    /// </remarks>
    public decimal? ExtendedChangePercent =>
        ExtendedPrice is { } extended && Last is { } last && last != 0
            ? (extended - last) / last * 100m
            : null;
}

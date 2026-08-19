namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// A push source of live trades, as distinct from <see cref="IQuoteProvider"/>, which polls.
/// Both exist: the poller still serves Tier 1 monitoring and supplies the fields no trade
/// stream carries (daily volume, previous close, extended hours).
/// </summary>
public interface IQuoteStream
{
    /// <summary>Replaces the subscription set. Safe to call before the stream connects.</summary>
    Task SetSymbolsAsync(IReadOnlyCollection<string> symbols, CancellationToken ct = default);

    /// <summary>Trades, until cancelled. Survives reconnects without ending.</summary>
    IAsyncEnumerable<Trade> ReadAllAsync(CancellationToken ct = default);
}

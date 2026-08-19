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

    /// <summary>
    /// Trades, until cancelled. Survives reconnects without ending.
    /// </summary>
    /// <remarks>
    /// Single-consumer: at most one concurrent enumeration is supported. Implementations may
    /// throw <see cref="InvalidOperationException"/> from a second concurrent call rather
    /// than silently splitting trades between two independent connections. Something that
    /// needs to fan trades out to many readers (e.g. one SSE connection per open browser
    /// tab) must enumerate this once itself and republish downstream — that fan-out point is
    /// <c>LiveQuoteCache</c>; nothing else should call <c>ReadAllAsync</c> directly.
    /// </remarks>
    IAsyncEnumerable<Trade> ReadAllAsync(CancellationToken ct = default);
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// The single source of truth for what every watched symbol is worth right now. Merges a
/// live trade stream with slow REST snapshots and fans the result out to SSE subscribers.
/// </summary>
/// <remarks>
/// Singleton, and touched from a websocket read loop, a background worker, and every open
/// browser connection at once, so every operation must be thread-safe.
/// </remarks>
public sealed class LiveQuoteCache(TimeProvider timeProvider)
{
    // Not read by the merge logic below, which derives everything from the timestamps on
    // its inputs — kept for the freshness reporting Task 9 adds, and for consistency with
    // the rest of Services/.
    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly ConcurrentDictionary<string, LiveQuote> _quotes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<Guid, Channel<LiveQuote>> _subscribers = new();

    public LiveQuote? Get(string symbol) =>
        _quotes.TryGetValue(symbol, out var quote) ? quote : null;

    public IReadOnlyCollection<LiveQuote> Snapshot() => _quotes.Values.ToArray();

    /// <summary>
    /// Applies a live tick. A trade older than the one already stored is discarded:
    /// providers do not guarantee ordering, and rewinding a price on a late-arriving
    /// message would show a stale number as if it were current.
    /// </summary>
    public void ApplyTrade(Trade trade)
    {
        var updated = _quotes.AddOrUpdate(
            trade.Symbol,
            _ => new LiveQuote(trade.Symbol.ToUpperInvariant(), trade.Price, trade.At),
            (_, existing) => existing.LastAt >= trade.At
                ? existing
                : existing with { Last = trade.Price, LastAt = trade.At });

        // Only publish when the tick actually changed something.
        if (updated.LastAt == trade.At)
        {
            Publish(updated);
        }
    }

    /// <summary>
    /// Applies a REST snapshot. Volume, previous close and extended-hours always land, but
    /// the snapshot's price only becomes Last if no fresher live tick has arrived — the
    /// poll runs minutes behind the stream and must never stomp it.
    /// </summary>
    /// <param name="session">
    /// The trading session the previous close belongs to. Stored so the worker can tell a
    /// current baseline from yesterday's; a stale one would silently skew every change
    /// percentage for a whole day.
    /// </param>
    public void ApplySnapshot(Quote quote, DateOnly session)
    {
        var updated = _quotes.AddOrUpdate(
            quote.Symbol,
            _ => new LiveQuote(
                quote.Symbol.ToUpperInvariant(),
                quote.Price, quote.At,
                quote.PreviousClose, quote.PreviousClose is null ? null : session,
                quote.Volume, quote.Volume is null ? null : quote.At,
                quote.ExtendedPrice, quote.ExtendedAt),
            (_, existing) => existing with
            {
                Last = existing.LastAt >= quote.At ? existing.Last : quote.Price,
                LastAt = existing.LastAt >= quote.At ? existing.LastAt : quote.At,
                PreviousClose = quote.PreviousClose ?? existing.PreviousClose,
                PreviousCloseSession = quote.PreviousClose is null
                    ? existing.PreviousCloseSession
                    : session,
                Volume = quote.Volume ?? existing.Volume,
                VolumeAt = quote.Volume is null ? existing.VolumeAt : quote.At,
                ExtendedPrice = quote.ExtendedPrice ?? existing.ExtendedPrice,
                ExtendedAt = quote.ExtendedAt ?? existing.ExtendedAt,
            });

        Publish(updated);
    }

    /// <summary>
    /// Which of <paramref name="symbols"/> lack a previous close for
    /// <paramref name="session"/> — never seen, or carried over from an earlier session.
    /// </summary>
    public IReadOnlyList<string> SymbolsNeedingPreviousClose(
        IEnumerable<string> symbols, DateOnly session) =>
        symbols
            .Where(s => Get(s) is not { } quote
                        || quote.PreviousClose is null
                        || quote.PreviousCloseSession != session)
            .ToList();

    public void Forget(string symbol) => _quotes.TryRemove(symbol, out _);

    /// <summary>
    /// One bounded channel per subscriber, dropping the oldest pending update when full.
    /// A browser on a slow connection must not back-pressure the websocket read loop, and
    /// for a price panel the newest value is the only one that matters anyway.
    /// </summary>
    /// <remarks>
    /// Registration happens here, synchronously, rather than inside the iterator below.
    /// An <c>async IAsyncEnumerable</c> method with a <c>yield return</c> is lazy: its body
    /// — including adding to <see cref="_subscribers"/> — would not run until the caller's
    /// first <c>MoveNextAsync()</c>, leaving a window right after subscribing where updates
    /// are published to no one and silently lost. Registering before any `await` closes it.
    /// </remarks>
    public IAsyncEnumerable<LiveQuote> SubscribeAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveQuote>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _subscribers[id] = channel;
        return ReadAsync(id, channel, ct);
    }

    private async IAsyncEnumerable<LiveQuote> ReadAsync(
        Guid id, Channel<LiveQuote> channel, [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var quote in channel.Reader.ReadAllAsync(ct))
            {
                yield return quote;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private void Publish(LiveQuote quote)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(quote);
        }
    }
}

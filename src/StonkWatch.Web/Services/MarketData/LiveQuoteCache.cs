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
///
/// All state — the quote table and the subscriber table — is guarded by a single
/// <see cref="_gate"/>. Nothing here ever awaits while holding it (a channel's
/// <c>TryWrite</c> never blocks, even on a full <see cref="BoundedChannelFullMode.DropOldest"/>
/// channel), so the lock is held only for plain in-memory work. A single lock, rather than
/// per-symbol locks or a lock-free structure, is deliberate: at the watchlist's scale
/// (dozens of symbols) contention is not a concern, and it is what lets install-then-publish
/// be one atomic step (see <see cref="ApplyTrade"/> / <see cref="ApplySnapshot"/>).
/// </remarks>
public sealed class LiveQuoteCache(TimeProvider timeProvider)
{
    // Not read by the merge logic below, which derives everything from the timestamps on
    // its inputs — kept for the freshness reporting Task 9 adds, and for consistency with
    // the rest of Services/.
    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly object _gate = new();
    private readonly Dictionary<string, LiveQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Channel<LiveQuote>> _subscribers = new();

    public LiveQuote? Get(string symbol)
    {
        lock (_gate)
        {
            return _quotes.TryGetValue(Normalize(symbol), out var quote) ? quote : null;
        }
    }

    /// <summary>
    /// Test-only visibility into subscriber-table size. There is no public-surface way to
    /// observe whether an unsubscribe actually removed its entry: <see cref="Publish"/>'s
    /// <c>TryWrite</c> on a leaked, completed channel just returns <see langword="false"/>,
    /// silently, so a leak has no externally visible effect except growing this count.
    /// </summary>
    internal int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public IReadOnlyCollection<LiveQuote> Snapshot()
    {
        lock (_gate)
        {
            // The only copy: with a plain Dictionary (unlike ConcurrentDictionary),
            // .Values is a lazy view rather than a pre-materialized snapshot, so this
            // ToArray() is the sole allocation needed to hand the caller something safe
            // to use after the lock is released.
            return _quotes.Values.ToArray();
        }
    }

    /// <summary>
    /// Applies a live tick. A trade strictly older than the one already stored is discarded:
    /// providers do not guarantee ordering, and rewinding a price on a late-arriving message
    /// would show a stale number as if it were current. A trade carrying the *same*
    /// timestamp as the one already stored still advances Last — two ticks can legitimately
    /// share a millisecond, and the newer message (by arrival, since it couldn't beat the
    /// clock) is the one to trust.
    /// </summary>
    public void ApplyTrade(Trade trade)
    {
        var symbol = Normalize(trade.Symbol);

        // Install and publish must happen as one atomic step. AddOrUpdate alone only makes
        // the install atomic; two ingest threads (this one and ApplySnapshot's) could then
        // install in one order and publish in the other, leaving every subscriber's last
        // frame disagreeing with what Get() returns. Holding _gate across both closes that.
        lock (_gate)
        {
            LiveQuote updated;
            bool changed;
            if (_quotes.TryGetValue(symbol, out var existing))
            {
                changed = existing.LastAt is not { } lastAt || trade.At >= lastAt;
                updated = changed ? existing with { Last = trade.Price, LastAt = trade.At } : existing;
            }
            else
            {
                updated = new LiveQuote(symbol, trade.Price, trade.At);
                changed = true;
            }

            _quotes[symbol] = updated;

            // Only publish when this call actually won the write — a discarded trade must
            // not fan out a quote that didn't change.
            if (changed)
            {
                Publish(updated);
            }
        }
    }

    /// <summary>
    /// Applies a REST snapshot. Volume, previous close and extended-hours land only when the
    /// snapshot itself is the freshest source for that field; the price only becomes Last if
    /// no fresher live tick has arrived — the poll runs minutes behind the stream and must
    /// never stomp it. Unlike <see cref="ApplyTrade"/>, a snapshot at the exact same
    /// timestamp as the stored Last does not win: a REST snapshot must never displace an
    /// equally-timestamped live tick.
    /// </summary>
    /// <param name="session">
    /// The trading session the previous close belongs to. Stored so the worker can tell a
    /// current baseline from yesterday's; a stale one would silently skew every change
    /// percentage for a whole day.
    /// </param>
    public void ApplySnapshot(Quote quote, DateOnly session)
    {
        var symbol = Normalize(quote.Symbol);

        lock (_gate)
        {
            var updated = _quotes.TryGetValue(symbol, out var existing)
                ? Merge(existing, quote, session)
                : Create(symbol, quote, session);

            _quotes[symbol] = updated;
            Publish(updated);
        }
    }

    private static LiveQuote Create(string symbol, Quote quote, DateOnly session)
    {
        // Extended price and its timestamp are taken as a unit — see Merge for why.
        var hasExtended = quote.ExtendedPrice is not null && quote.ExtendedAt is not null;

        return new LiveQuote(
            symbol,
            quote.Price, quote.At,
            quote.PreviousClose, quote.PreviousClose is null ? null : session,
            quote.Volume, quote.Volume is null ? null : quote.At,
            hasExtended ? quote.ExtendedPrice : null,
            hasExtended ? quote.ExtendedAt : null);
    }

    private static LiveQuote Merge(LiveQuote existing, Quote quote, DateOnly session)
    {
        // Ties go to what's already stored — see the ApplySnapshot doc comment.
        var priceFresher = existing.LastAt is null || quote.At > existing.LastAt;

        // Volume has no timestamp of its own on Quote; it shares the quote's overall At.
        // Without this check, two REST polls in flight (a retry, or a slow response
        // overtaken by the next cycle) can land out of order and volume — which only ever
        // climbs intraday — visibly goes backwards.
        var volumeFresher = quote.Volume is not null
            && (existing.VolumeAt is null || quote.At > existing.VolumeAt);

        // ExtendedPrice and ExtendedAt are taken as a unit, both or neither, and judged by
        // ExtendedAt's own clock (it is the actual timestamp of that trade, unlike Volume).
        // TwelveDataQuoteProvider parses the two independently, so a payload can produce
        // {ExtendedPrice: 65.20, ExtendedAt: null}; merging the fields independently would
        // let a fresh price keep a stale timestamp and mislabel when it happened.
        var extendedFresher = quote.ExtendedPrice is not null && quote.ExtendedAt is not null
            && (existing.ExtendedAt is null || quote.ExtendedAt > existing.ExtendedAt);

        return existing with
        {
            Last = priceFresher ? quote.Price : existing.Last,
            LastAt = priceFresher ? quote.At : existing.LastAt,
            PreviousClose = quote.PreviousClose ?? existing.PreviousClose,
            PreviousCloseSession = quote.PreviousClose is null
                ? existing.PreviousCloseSession
                : session,
            Volume = volumeFresher ? quote.Volume : existing.Volume,
            VolumeAt = volumeFresher ? quote.At : existing.VolumeAt,
            ExtendedPrice = extendedFresher ? quote.ExtendedPrice : existing.ExtendedPrice,
            ExtendedAt = extendedFresher ? quote.ExtendedAt : existing.ExtendedAt,
        };
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

    public void Forget(string symbol)
    {
        lock (_gate)
        {
            _quotes.Remove(Normalize(symbol));
        }
    }

    /// <summary>
    /// One bounded channel per subscriber, dropping the oldest pending update when full. A
    /// browser on a slow connection must not back-pressure the ingest path, and for a price
    /// panel the newest value is the only one that matters anyway.
    /// </summary>
    /// <remarks>
    /// Cold and per-enumeration: calling this method does no work itself, and each call to
    /// the returned enumerable's <c>GetAsyncEnumerator</c> creates and registers its own
    /// channel. Two things that would go wrong otherwise:
    /// <list type="bullet">
    /// <item>Registering once, here, and sharing that one channel across every enumeration
    /// of the result — enumerating it twice would hand two readers a channel opened with
    /// <c>SingleReader = true</c> (undefined behavior), and whichever enumeration finished
    /// first would unregister the subscriber out from under the other, which would then
    /// block forever on a channel nobody writes to or completes: an SSE connection held
    /// open and silent.</item>
    /// <item>Registering eagerly inside this method (rather than per enumeration) would also
    /// leak: an <see cref="IAsyncEnumerable{T}"/> that is obtained but never enumerated —
    /// e.g. something throws between this call and Task 7's first <c>MoveNextAsync</c> —
    /// would orphan an entry in <see cref="_subscribers"/> forever, and every future
    /// <see cref="Publish"/> would pay for it on every tick.</item>
    /// </list>
    /// Registration still happens before any caller-issued <c>ApplyTrade</c>/<c>ApplySnapshot</c>
    /// that follows the first <c>GetAsyncEnumerator</c> call, which is all the ordering the
    /// subscribe tests below need.
    /// </remarks>
    public IAsyncEnumerable<LiveQuote> SubscribeAsync(CancellationToken ct) => new Subscription(this, ct);

    private sealed class Subscription(LiveQuoteCache cache, CancellationToken subscribeToken)
        : IAsyncEnumerable<LiveQuote>
    {
        public IAsyncEnumerator<LiveQuote> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            cache.Subscribe(cancellationToken == default ? subscribeToken : cancellationToken);
    }

    private IAsyncEnumerator<LiveQuote> Subscribe(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveQuote>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_gate)
        {
            _subscribers[id] = channel;
        }

        return ReadAsync(id, channel, ct).GetAsyncEnumerator(ct);
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
            lock (_gate)
            {
                _subscribers.Remove(id);
            }

            // A clean completion (rather than only ever an OperationCanceledException) so
            // Task 7's SSE handler sees an orderly end regardless of why this reader stopped.
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Must only be called while holding <see cref="_gate"/>.</summary>
    private void Publish(LiveQuote quote)
    {
        foreach (var kvp in _subscribers)
        {
            kvp.Value.Writer.TryWrite(quote);
        }
    }

    private static string Normalize(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.Trim().ToUpperInvariant();
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// Holds one Finnhub websocket for the whole process and republishes its trades. Every
/// browser tab reads from <see cref="LiveQuoteCache"/> downstream of this, so the
/// provider's 50-symbol cap limits the watchlist, not the number of open tabs.
/// </summary>
public sealed class FinnhubQuoteStream(
    IWebSocketConnectionFactory connections,
    IOptions<FinnhubOptions> options,
    TimeProvider timeProvider,
    ILogger<FinnhubQuoteStream> logger) : IQuoteStream
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly FinnhubOptions _options = options.Value;
    private readonly Channel<Trade> _trades = Channel.CreateBounded<Trade>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private IWebSocketConnection? _connection;
    private int _enumerating;

    public async Task SetSymbolsAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var wanted = symbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await _gate.WaitAsync(ct);
        try
        {
            var added = wanted.Except(_symbols, StringComparer.OrdinalIgnoreCase).ToArray();
            var removed = _symbols.Except(wanted, StringComparer.OrdinalIgnoreCase).ToArray();
            _symbols = wanted;

            // If nothing is connected yet the new set is picked up on the next connect.
            if (_connection is not { } connection)
            {
                return;
            }

            try
            {
                foreach (var symbol in added)
                {
                    await SendAsync(connection, "subscribe", symbol, ct);
                }
                foreach (var symbol in removed)
                {
                    await SendAsync(connection, "unsubscribe", symbol, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The socket can die between the last read and this write. _symbols is
                // already updated above, so the next connect's replay in RunConnectionAsync
                // sends the correct set from scratch — there is nothing more to do here than
                // let the caller's update succeed instead of throwing a transport exception
                // out of a page or endpoint that has no idea how to map it.
                logger.LogWarning(
                    ex, "Failed to update Finnhub subscriptions on a live connection; will resync on reconnect");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<Trade> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Single-consumer by design: one Finnhub socket, one shared trade channel. A second
        // concurrent enumeration would start a second pump and a second socket, and the two
        // would silently split the trades between them (see IQuoteStream.ReadAllAsync).
        if (Interlocked.CompareExchange(ref _enumerating, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "FinnhubQuoteStream.ReadAllAsync supports only one concurrent consumer.");
        }

        var pump = Task.Run(() => PumpAsync(ct), ct);
        try
        {
            await foreach (var trade in _trades.Reader.ReadAllAsync(ct))
            {
                yield return trade;
            }
        }
        finally
        {
            Volatile.Write(ref _enumerating, 0);
            await pump.WaitAsync(TimeSpan.FromSeconds(5), timeProvider, CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Connect, read, and on any failure back off and start again. This loop must never
    /// throw: one unhandled exception here silently freezes the sidebar for the life of
    /// the process, exactly as it would in PriceCheckWorker.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;

        while (!ct.IsCancellationRequested)
        {
            var connectedAt = timeProvider.GetUtcNow();
            try
            {
                await RunConnectionAsync(ct);

                // A session that stayed up for a while is healthy: reset the backoff to the
                // floor. A session that never really got going — Finnhub accepts and then
                // immediately hangs up once a connection or symbol limit is hit — must not
                // reset it, or a bad key or a hit limit turns into a tight reconnect loop
                // instead of backing off.
                if (timeProvider.GetUtcNow() - connectedAt >= MinBackoff)
                {
                    backoff = MinBackoff;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never log the URL: the API key is a query parameter on it.
                logger.LogWarning(ex, "Finnhub stream failed; reconnecting in {Backoff}", backoff);
            }

            try
            {
                // Always paced by backoff, even after a clean close — an instantly-hanging-up
                // peer is exactly the case above that must not spin unthrottled.
                await Task.Delay(backoff, timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = backoff < MaxBackoff
                ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks))
                : MaxBackoff;
        }
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        var connection = connections.Create();
        await using var _ = connection;

        await connection.ConnectAsync(
            new Uri($"{_options.WebSocketUrl}?token={Uri.EscapeDataString(_options.ApiKey)}"), ct);

        try
        {
            // Subscriptions are per-connection, so the full set is replayed on every connect,
            // held under the gate so a concurrent SetSymbolsAsync can't interleave its own
            // subscribe/unsubscribe with this replay.
            await _gate.WaitAsync(ct);
            try
            {
                _connection = connection;
                foreach (var symbol in _symbols)
                {
                    await SendAsync(connection, "subscribe", symbol, ct);
                }
            }
            finally
            {
                _gate.Release();
            }

            while (!ct.IsCancellationRequested)
            {
                var frame = await connection.ReceiveAsync(ct);
                if (frame is null)
                {
                    return;   // peer closed; PumpAsync reconnects
                }

                foreach (var trade in FinnhubMessageParser.ParseTrades(frame))
                {
                    _trades.Writer.TryWrite(trade);
                }
            }
        }
        finally
        {
            // Whatever happened above — clean close, cancellation, or a failed send during
            // the subscribe replay — this connection must stop being the "live" one. Leaving
            // a stale pointer here after `await using` disposes the socket is exactly the bug
            // this closes: the next SetSymbolsAsync would otherwise try to write to a dead
            // connection instead of quietly waiting for the reconnect to replay real state.
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static Task SendAsync(
        IWebSocketConnection connection, string type, string symbol, CancellationToken ct) =>
        connection.SendAsync(
            JsonSerializer.Serialize(new { type, symbol }), ct);
}

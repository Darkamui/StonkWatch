using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

/// <summary>A scripted websocket: tests push frames in and read the JSON sent out.</summary>
public sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    private readonly object _sentGate = new();
    private readonly List<string> _sent = [];

    /// <summary>
    /// When set, the next <see cref="SendAsync"/> call throws this instead of recording,
    /// then clears itself. Simulates a socket that dies right after the handshake.
    /// </summary>
    public Exception? FailNextSend { get; set; }

    /// <summary>
    /// Snapshotted under a lock: this is written from the pump's background thread and read
    /// from the test thread, and a plain <c>List&lt;T&gt;</c> is not safe for that.
    /// </summary>
    public IReadOnlyList<string> Sent
    {
        get { lock (_sentGate) { return _sent.ToArray(); } }
    }

    public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

    public Task SendAsync(string json, CancellationToken ct)
    {
        if (FailNextSend is { } ex)
        {
            FailNextSend = null;
            throw ex;
        }

        lock (_sentGate) { _sent.Add(json); }
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) =>
        await _incoming.Reader.ReadAsync(ct);

    public void Push(string frame) => _incoming.Writer.TryWrite(frame);

    /// <summary>Simulates the peer hanging up.</summary>
    public void Close() => _incoming.Writer.TryWrite(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    private readonly object _gate = new();
    private readonly List<FakeWebSocketConnection> _created = [];

    /// <summary>
    /// When set, applied to the next connection this factory creates (then cleared) —
    /// lets a test fail the very first connection's subscribe replay.
    /// </summary>
    public Exception? FailNextSendOnNextConnection { get; set; }

    /// <summary>
    /// Snapshotted under a lock for the same reason as <see cref="FakeWebSocketConnection.Sent"/>.
    /// </summary>
    public IReadOnlyList<FakeWebSocketConnection> Created
    {
        get { lock (_gate) { return _created.ToArray(); } }
    }

    public IWebSocketConnection Create()
    {
        var connection = new FakeWebSocketConnection();
        if (FailNextSendOnNextConnection is { } ex)
        {
            connection.FailNextSend = ex;
            FailNextSendOnNextConnection = null;
        }

        lock (_gate) { _created.Add(connection); }
        return connection;
    }
}

public class FinnhubQuoteStreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    private static (FinnhubQuoteStream Stream, FakeWebSocketConnectionFactory Factory, FakeTimeProvider Time) New()
    {
        var factory = new FakeWebSocketConnectionFactory();
        var time = new FakeTimeProvider(Now);
        var options = Options.Create(new FinnhubOptions { ApiKey = "test-key" });
        return (
            new FinnhubQuoteStream(factory, options, time, NullLogger<FinnhubQuoteStream>.Instance),
            factory,
            time);
    }

    /// <summary>Spins until <paramref name="condition"/> holds or the timeout expires.</summary>
    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }

    [Fact]
    public async Task ReadAllAsync_yields_trades_from_the_socket()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync();

        await WaitFor(() => factory.Created.Count > 0, "the stream to connect");
        factory.Created[0].Push("""
            {"type":"trade","data":[{"s":"ASTS","p":67.61,"t":1787059800000}]}
            """);

        Assert.True(await next);
        Assert.Equal(67.61m, enumerator.Current.Price);
    }

    [Fact]
    public async Task Connecting_subscribes_to_every_symbol()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS", "SPCE"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = enumerator.MoveNextAsync();

        await WaitFor(
            () => factory.Created.Count > 0 && factory.Created[0].Sent.Count >= 2,
            "both subscribe frames to be sent");

        // Exact frames, not substring checks: a mutant that sends "unsubscribe" for every
        // symbol during the connect replay must fail this — a loose Contains("subscribe")
        // check would not, since "unsubscribe".Contains("subscribe") is true.
        Assert.Contains("""{"type":"subscribe","symbol":"ASTS"}""", factory.Created[0].Sent);
        Assert.Contains("""{"type":"subscribe","symbol":"SPCE"}""", factory.Created[0].Sent);
    }

    [Fact]
    public async Task A_dropped_connection_reconnects_and_resubscribes()
    {
        var (stream, factory, time) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync();

        await WaitFor(() => factory.Created.Count > 0, "the first connection");
        factory.Created[0].Close();

        // Backoff must actually be waited out, not skipped: right after the close, before
        // any clock advance, no second connection can legitimately exist yet.
        Assert.Single(factory.Created);

        // Advance the fake clock from inside the poll predicate itself rather than once
        // before it. FakeTimeProvider computes a Task.Delay's due time from the clock at the
        // moment the delay is registered, which the pump does on a background thread at a
        // point this test can't observe — a single Advance() call risks landing before that
        // registration and then waiting for an Advance() that never comes. Advancing on
        // every poll instead removes the ordering dependency entirely: however late the
        // pump registers its delay, the next iteration's Advance() lands after it.
        await WaitFor(
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(30));
                return factory.Created.Count > 1;
            },
            "a second connection");

        // Finnhub subscriptions are per-connection. A reconnect that forgets its symbols
        // leaves a permanently frozen sidebar with no error anywhere.
        await WaitFor(
            () => factory.Created[1].Sent.Contains("""{"type":"subscribe","symbol":"ASTS"}"""),
            "the symbol to be re-subscribed on the new connection");

        factory.Created[1].Push("""
            {"type":"trade","data":[{"s":"ASTS","p":70.00,"t":1787059900000}]}
            """);

        Assert.True(await next);
        Assert.Equal(70.00m, enumerator.Current.Price);
    }

    [Fact]
    public async Task SetSymbolsAsync_unsubscribes_a_removed_symbol_on_a_live_connection()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS", "SPCE"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = enumerator.MoveNextAsync();
        await WaitFor(
            () => factory.Created.Count > 0 && factory.Created[0].Sent.Count >= 2,
            "the initial subscriptions");

        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        await WaitFor(
            () => factory.Created[0].Sent.Contains("""{"type":"unsubscribe","symbol":"SPCE"}"""),
            "SPCE to be unsubscribed");
    }

    [Fact]
    public async Task A_removed_symbol_stays_removed_across_a_reconnect()
    {
        var (stream, factory, time) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS", "SPCE"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = enumerator.MoveNextAsync();
        await WaitFor(
            () => factory.Created.Count > 0 && factory.Created[0].Sent.Count >= 2,
            "the initial subscriptions");

        await stream.SetSymbolsAsync(["ASTS"], cts.Token);   // SPCE removed
        await WaitFor(
            () => factory.Created[0].Sent.Contains("""{"type":"unsubscribe","symbol":"SPCE"}"""),
            "SPCE to be unsubscribed");

        factory.Created[0].Close();
        await WaitFor(
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(30));
                return factory.Created.Count > 1;
            },
            "a second connection");

        await WaitFor(
            () => factory.Created[1].Sent.Contains("""{"type":"subscribe","symbol":"ASTS"}"""),
            "ASTS to be re-subscribed on reconnect");

        // The mutant this guards against: replacing `_symbols = wanted` with a union would
        // silently re-subscribe a symbol the caller explicitly removed, burning the free
        // tier's symbol cap on a ticker nobody wants anymore.
        Assert.DoesNotContain(factory.Created[1].Sent, s => s.Contains("\"SPCE\""));
    }

    [Fact]
    public async Task A_failed_subscribe_replay_clears_the_connection_instead_of_leaving_it_stale()
    {
        var (stream, factory, time) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        factory.FailNextSendOnNextConnection = new IOException("connection reset");
        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        var enumerator = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = enumerator.MoveNextAsync();

        // The first connection's subscribe replay fails immediately (a socket that closes
        // right after the handshake, or any transient write error). Before the fix,
        // `_connection` stayed pointed at this connection forever — the replay's own
        // try/finally only released the gate, it never reached the code that clears the
        // field — so this call would send straight to the dead connection instead of
        // deferring to the reconnect.
        await WaitFor(() => factory.Created.Count > 0, "the first (failing) connection");
        await stream.SetSymbolsAsync(["ASTS", "SPCE"], cts.Token);

        Assert.DoesNotContain(factory.Created[0].Sent, s => s.Contains("SPCE"));

        // The pump backs off and reconnects; the reconnect's replay must carry the full,
        // up-to-date symbol set, proving `_symbols` itself stayed correct even though the
        // first connection's own subscribe never went through.
        await WaitFor(
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(30));
                return factory.Created.Count > 1;
            },
            "a second connection");

        await WaitFor(
            () => factory.Created[1].Sent.Contains("""{"type":"subscribe","symbol":"ASTS"}""")
                  && factory.Created[1].Sent.Contains("""{"type":"subscribe","symbol":"SPCE"}"""),
            "both symbols to be subscribed on the reconnect");
    }

    [Fact]
    public async Task ReadAllAsync_throws_when_a_second_consumer_enumerates_concurrently()
    {
        var (stream, factory, _) = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.SetSymbolsAsync(["ASTS"], cts.Token);

        var first = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = first.MoveNextAsync();
        await WaitFor(() => factory.Created.Count > 0, "the first consumer to start the pump");

        var second = stream.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
    }
}

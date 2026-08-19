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

    public List<string> Sent { get; } = [];
    public Uri? ConnectedTo { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectedTo = uri;
        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        Sent.Add(json);
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
    public List<FakeWebSocketConnection> Created { get; } = [];

    public IWebSocketConnection Create()
    {
        var connection = new FakeWebSocketConnection();
        Created.Add(connection);
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

    /// <summary>
    /// Spins until <paramref name="condition"/> holds or the timeout expires. Always yields
    /// at least once before the first check: some conditions here (e.g. "connection count is
    /// still 1, meaning backoff has started") are already true the instant this is called, so
    /// without a yield the caller would race straight past the background pump loop before it
    /// gets a turn on the thread pool.
    /// </summary>
    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            if (condition()) return;
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

        Assert.Contains(factory.Created[0].Sent, s => s.Contains("\"ASTS\""));
        Assert.Contains(factory.Created[0].Sent, s => s.Contains("\"SPCE\""));
        Assert.All(factory.Created[0].Sent, s => Assert.Contains("subscribe", s));
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

        // Backoff is driven by the injected TimeProvider, so the test never really sleeps.
        await WaitFor(() => factory.Created.Count == 1, "the stream to enter backoff");
        time.Advance(TimeSpan.FromSeconds(30));

        await WaitFor(() => factory.Created.Count > 1, "a second connection");

        // Finnhub subscriptions are per-connection. A reconnect that forgets its symbols
        // leaves a permanently frozen sidebar with no error anywhere.
        await WaitFor(
            () => factory.Created[1].Sent.Any(s => s.Contains("\"ASTS\"")),
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
            () => factory.Created[0].Sent.Any(
                s => s.Contains("unsubscribe") && s.Contains("\"SPCE\"")),
            "SPCE to be unsubscribed");
    }
}

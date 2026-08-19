using System.Net.WebSockets;
using System.Text;

namespace StonkWatch.Web.Services.MarketData;

/// <summary>
/// A one-message-at-a-time view of a websocket. Exists so the reconnect and re-subscribe
/// logic in <see cref="FinnhubQuoteStream"/> can be tested without a network.
/// </summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>The next text message, or null once the peer has closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

public interface IWebSocketConnectionFactory
{
    IWebSocketConnection Create();
}

public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[16 * 1024];

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

    public Task SendAsync(string json, CancellationToken ct) => _socket.SendAsync(
        Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(_buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            message.Write(_buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ClientWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    public IWebSocketConnection Create() => new ClientWebSocketConnection();
}

using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.Notifications;

namespace StonkWatch.Web.Tests;

/// <summary>Returns whatever prices a test dictates, or throws to simulate an outage.</summary>
public sealed class FakeQuoteProvider : IQuoteProvider
{
    private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset QuotedAt { get; set; } = new(2026, 7, 31, 14, 30, 0, TimeSpan.Zero);
    public Exception? ThrowOnCall { get; set; }
    public int CallCount { get; private set; }
    public List<string> LastRequestedSymbols { get; } = [];

    public FakeQuoteProvider Set(string symbol, decimal price)
    {
        _prices[symbol] = price;
        return this;
    }

    public void Clear() => _prices.Clear();

    public Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        CallCount++;
        LastRequestedSymbols.Clear();
        LastRequestedSymbols.AddRange(symbols);

        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        IReadOnlyDictionary<string, Quote> result = symbols
            .Where(_prices.ContainsKey)
            .ToDictionary(
                s => s,
                s => new Quote(s, _prices[s], QuotedAt),
                StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(result);
    }
}

/// <summary>Captures notifications instead of sending them.</summary>
public sealed class RecordingNotifier : INotifier
{
    public List<NotificationMessage> Sent { get; } = [];
    public Exception? ThrowOnSend { get; set; }

    public NotificationMessage? Last => Sent.Count > 0 ? Sent[^1] : null;

    public Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

using Microsoft.Extensions.Logging;

namespace StonkWatch.Web.Tests;

/// <summary>
/// Records what a component logged, so tests can assert on it — chiefly that a message was
/// emitted at all, and that no secret reached it.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _sync = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_sync) { return [.. _entries]; } }
    }

    /// <summary>
    /// Everything logged, joined. Includes the raw state object as well as the rendered
    /// message: a structured argument can carry a value the format string never names.
    /// </summary>
    public string AllText
    {
        get
        {
            lock (_sync)
            {
                return string.Join("\n", _entries.Select(
                    e => $"{e.Level}: {e.Message} | {e.State} | {e.Exception}"));
            }
        }
    }

    public IReadOnlyList<LogEntry> AtLevel(LogLevel level) =>
        [.. Entries.Where(e => e.Level == level)];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            _entries.Add(new LogEntry(
                logLevel, formatter(state, exception), state?.ToString(), exception));
        }
    }

    public record LogEntry(LogLevel Level, string Message, string? State, Exception? Exception)
    {
        /// <summary>Message, structured state and exception together — the whole leak surface.</summary>
        public string Text => $"{Message} | {State} | {Exception}";
    }
}

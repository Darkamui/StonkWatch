using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.Watchlist;

public class LiveWatchlistOptions
{
    public const string SectionName = "LiveWatchlist";

    /// <summary>
    /// Off by default, for the same reason <see cref="Monitoring.MonitoringOptions.Enabled"/>
    /// is: a developer running locally should not open upstream sockets or spend API credits.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// One batched Questrade quotes call covers the whole watchlist, so the cap isn't about
    /// staying under a request limit — 50 keeps both the request and the sidebar itself sane.
    /// </summary>
    [Range(1, 500)]
    public int MaxSymbols { get; set; } = 50;

    /// <summary>How often the watchlist sidebar polls Questrade for fresh quotes.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 3;

    /// <summary>
    /// How long the SSE stream can go without a real data event before it sends a
    /// keepalive. <c>Program.cs</c> documents a reverse proxy in front, and proxies commonly
    /// drop idle upstream connections around 60s; outside market hours this stream can
    /// otherwise sit silent indefinitely. Configurable (rather than a hardcoded constant) so
    /// a test can drive it sub-second.
    /// </summary>
    [Range(1, 300)]
    public int KeepaliveSeconds { get; set; } = 20;
}

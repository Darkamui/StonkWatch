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
    /// How often to poll while the market is fully closed — no regular session and no extended
    /// hours. Pre- and post-market both use <see cref="PollSeconds"/>: prices genuinely move
    /// then, and watching them is the point.
    /// </summary>
    /// <remarks>
    /// Slow rather than zero on purpose. <see cref="MarketData.LiveQuoteCache"/> lives in
    /// process memory only, so a container restart on a Saturday would leave every row at an
    /// em dash until Monday's pre-market if closed ticks stopped entirely. One call every five
    /// minutes — and only while somebody actually has the sidebar open — refills a cold cache
    /// without pretending anything is moving.
    /// </remarks>
    [Range(5, 3600)]
    public int ClosedPollSeconds { get; set; } = 300;

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

using Microsoft.Extensions.Options;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.MarketData.Questrade;
using StonkWatch.Web.Services.Monitoring;

namespace StonkWatch.Web.Services.Watchlist;

/// <summary>
/// Runs <see cref="LiveWatchlistPollJob"/> on a timer. Structured like
/// <see cref="Monitoring.PriceCheckWorker"/> — a <see cref="PeriodicTimer"/> built with the
/// injected <see cref="TimeProvider"/>, a fresh DI scope per tick — with three differences: a
/// tick is skipped entirely while nobody has the sidebar open, ticks thin out to
/// <see cref="LiveWatchlistOptions.ClosedPollSeconds"/> while the market is closed, and a
/// skipped tick still touches the Questrade authenticator often enough to keep the refresh
/// token from expiring.
/// </summary>
/// <remarks>
/// Questrade's refresh token expires after 3 idle days. If the sidebar goes unopened over a
/// long weekend and every tick is skipped, nothing would ever refresh it and the user would
/// have to re-authorize by hand. So on a skipped tick, once more than
/// <see cref="TokenKeepaliveHours"/> have passed since the authenticator was last touched
/// (by a real tick or a previous keepalive), this calls
/// <see cref="IQuestradeAuthenticator.GetSessionAsync"/> anyway and discards the result — a
/// refresh rotates the token and resets its 3-day clock. Any failure here is logged and
/// swallowed; this path must never take the loop down.
/// </remarks>
public class LiveWatchlistPollWorker(
    IServiceScopeFactory scopeFactory,
    LiveQuoteCache cache,
    IQuestradeAuthenticator authenticator,
    TimeProvider timeProvider,
    IOptions<LiveWatchlistOptions> options,
    ILogger<LiveWatchlistPollWorker> logger) : BackgroundService
{
    private const int TokenKeepaliveHours = 12;

    /// <summary>
    /// The last time this worker touched the authenticator, via either a real tick or a
    /// keepalive call. Null means unknown — a process just started with no evidence of a
    /// recent refresh — which is treated as overdue: an extra refresh costs nothing, but
    /// under-counting risks the 3-day expiry.
    /// </summary>
    private DateTimeOffset? _lastAuthTouch;

    private bool _skipping;

    /// <summary>
    /// When the last closed-market poll ran, or null while the market is open in any phase.
    /// Cleared on every open tick so the first tick after the closing bell polls at once
    /// rather than inheriting a timer from the previous night.
    /// </summary>
    private DateTimeOffset? _lastClosedPoll;

    /// <summary>
    /// The phase of the last tick that got as far as looking, so the transitions are logged
    /// once each instead of on every tick. Null until the first such tick.
    /// </summary>
    private MarketPhase? _lastPhase;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);

        // Logged after the timer is armed, not before: this line is what "started" means, and
        // a test driving a FakeTimeProvider waits on it before advancing the clock. Logged
        // first, it would announce a worker that can still miss the tick it is told about.
        logger.LogInformation("Live watchlist poll worker started; interval {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cache.SubscriberCount == 0)
            {
                if (!_skipping)
                {
                    logger.LogInformation("No live watchlist subscribers; skipping poll ticks.");
                    _skipping = true;
                }

                await KeepTokenAliveIfDueAsync(stoppingToken);
                continue;
            }

            if (_skipping)
            {
                logger.LogInformation("Live watchlist subscriber connected; resuming poll ticks.");
                _skipping = false;
            }

            var now = timeProvider.GetUtcNow();
            var phase = MarketCalendar.Phase(now);

            if (phase != _lastPhase)
            {
                logger.LogInformation(
                    "Market phase is {Phase}; polling every {Seconds}s.",
                    phase,
                    phase == MarketPhase.Closed
                        ? options.Value.ClosedPollSeconds
                        : options.Value.PollSeconds);
                _lastPhase = phase;
            }

            if (phase == MarketPhase.Closed)
            {
                var closedInterval = TimeSpan.FromSeconds(options.Value.ClosedPollSeconds);
                if (_lastClosedPoll is { } lastClosed && now - lastClosed < closedInterval)
                {
                    // Still a skipped tick as far as the token is concerned: a closed stretch
                    // runs three days over a holiday weekend, which is exactly the refresh
                    // token's idle lifetime.
                    await KeepTokenAliveIfDueAsync(stoppingToken);
                    continue;
                }

                // Stamped before the poll, not after. A slow or failing Questrade call must
                // not turn into a retry every PollSeconds for as long as it keeps failing.
                _lastClosedPoll = now;
            }
            else
            {
                _lastClosedPoll = null;
            }

            // A real tick touches the authenticator itself (the resolver and quote client
            // both call GetSessionAsync), so this resets the keepalive clock regardless of
            // whether the tick itself succeeds.
            _lastAuthTouch = now;

            try
            {
                // CreateScope() and resolving the job both live inside this try: a scope the
                // DI container fails to build is exactly as much "one bad tick" as a job that
                // throws once it's running, and must not kill the loop for the rest of the
                // process either.
                using var scope = scopeFactory.CreateScope();
                var job = scope.ServiceProvider.GetRequiredService<LiveWatchlistPollJob>();

                await job.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The job already handles its own known failure modes; this is the last line
                // of defence. An escaping exception would kill the loop for the life of the
                // process.
                logger.LogError(ex, "Unhandled failure in the live watchlist poll tick");
            }
        }

        logger.LogInformation("Live watchlist poll worker stopped");
    }

    private async Task KeepTokenAliveIfDueAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (_lastAuthTouch is { } last && now - last < TimeSpan.FromHours(TokenKeepaliveHours))
        {
            return;
        }

        try
        {
            await authenticator.GetSessionAsync(ct);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately: this path exists only to keep a refresh token alive
            // while nobody is watching, and must never take the loop down. A failure here
            // (including QuestradeReauthorizationRequiredException) is exactly as actionable
            // as any other Questrade failure, and the authenticator itself already logs the
            // specifics without leaking a token.
            logger.LogWarning(
                ex, "Token keepalive refresh failed while the live watchlist has no subscribers.");
        }
        finally
        {
            // Set even on failure: retrying every tick (as often as every few seconds) would
            // be noisy and pointless if the failure isn't transient, and the loop still has up
            // to TokenKeepaliveHours of slack before the real 3-day deadline matters.
            _lastAuthTouch = timeProvider.GetUtcNow();
        }
    }
}

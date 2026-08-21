using Microsoft.Extensions.Options;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Services.Watchlist;

/// <summary>
/// Runs <see cref="LiveWatchlistPollJob"/> on a timer. Structured like
/// <see cref="Monitoring.PriceCheckWorker"/> — a <see cref="PeriodicTimer"/> built with the
/// injected <see cref="TimeProvider"/>, a fresh DI scope per tick — with two differences: a
/// tick is skipped entirely while nobody has the sidebar open, and a skipped tick still touches
/// the Questrade authenticator often enough to keep the refresh token from expiring.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollSeconds);
        logger.LogInformation("Live watchlist poll worker started; interval {Interval}", interval);

        using var timer = new PeriodicTimer(interval, timeProvider);

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

            using var scope = scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<LiveWatchlistPollJob>();

            // A real tick touches the authenticator itself (the resolver and quote client
            // both call GetSessionAsync), so this resets the keepalive clock regardless of
            // whether the tick itself succeeds.
            _lastAuthTouch = timeProvider.GetUtcNow();

            try
            {
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

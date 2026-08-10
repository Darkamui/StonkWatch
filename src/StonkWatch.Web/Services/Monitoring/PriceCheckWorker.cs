using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.Monitoring;

/// <summary>
/// Runs <see cref="PriceCheckJob"/> on a timer. Deliberately contains nothing but scheduling —
/// all the logic lives in the job, which is scoped and testable.
/// </summary>
public class PriceCheckWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<MonitoringOptions> options,
    ILogger<PriceCheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.IntervalMinutes);
        logger.LogInformation("Price check worker started; interval {Interval}", interval);

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

            // The worker is a singleton but DbContext is scoped, so each tick needs its own
            // scope — resolving the job directly would fail on the second tick.
            using var scope = scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<PriceCheckJob>();

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
                // The job already records its own failures; this is the last line of defence.
                // An escaping exception would kill the loop for the life of the process.
                logger.LogError(ex, "Unhandled failure in the price check tick");
            }
        }

        logger.LogInformation("Price check worker stopped");
    }
}

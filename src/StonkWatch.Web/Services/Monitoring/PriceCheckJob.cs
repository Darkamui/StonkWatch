using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.Notifications;

namespace StonkWatch.Web.Services.Monitoring;

/// <summary>
/// One price-check cycle: fetch quotes, compare them to each candidate's levels, record what
/// crossed, and email a single digest. Scoped, so it owns a <see cref="StonkWatchDbContext"/>
/// for the duration of the tick.
/// </summary>
public class PriceCheckJob(
    StonkWatchDbContext db,
    IQuoteProvider quotes,
    INotifier notifier,
    TimeProvider timeProvider,
    IOptions<MonitoringOptions> monitoringOptions,
    IOptions<AppOptions> appOptions,
    ILogger<PriceCheckJob> logger)
{
    private readonly MonitoringOptions _options = monitoringOptions.Value;

    public async Task<JobRun> RunAsync(CancellationToken ct = default)
    {
        var startedAt = timeProvider.GetUtcNow();
        var run = new JobRun
        {
            Id = Guid.NewGuid(),
            Job = JobNames.PriceCheck,
            StartedAt = startedAt,
            Status = JobStatus.Running
        };

        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            if (!_options.IgnoreMarketHours && !MarketCalendar.IsOpen(startedAt))
            {
                run.SkipReason = MarketCalendar.DescribeClosed(startedAt);
                logger.LogDebug("Price check skipped: {Reason}", run.SkipReason);
            }
            else
            {
                await ExecuteAsync(run, ct);
            }

            run.Status = JobStatus.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = JobStatus.Failed;
            run.Error = ex.Message;
            logger.LogError(ex, "Price check failed");
        }
        finally
        {
            run.FinishedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return run;
    }

    private async Task ExecuteAsync(JobRun run, CancellationToken ct)
    {
        var candidates = await db.Candidates
            .Include(c => c.Alerts)
            .Where(c => c.Status != CandidateStatus.Invalidated)
            .ToListAsync(ct);

        // Candidates with no levels cost an API call and can never cross anything.
        var monitored = candidates.Where(c => LevelEvaluator.LevelsFor(c).Count > 0).ToList();
        if (monitored.Count == 0)
        {
            run.SkipReason = "No candidates with levels to monitor";
            return;
        }

        var prices = await quotes.GetQuotesAsync(monitored.Select(c => c.Ticker).ToList(), ct);
        var now = timeProvider.GetUtcNow();

        foreach (var candidate in monitored)
        {
            if (!prices.TryGetValue(candidate.Ticker, out var quote))
            {
                continue;
            }

            run.CandidatesChecked++;

            foreach (var crossing in LevelEvaluator.Evaluate(candidate, candidate.LastQuote, quote.Price))
            {
                UpsertAlert(candidate, crossing, quote.Price, now);
                run.AlertsFired++;
            }

            ReArmClearedAlerts(candidate, quote.Price, now);

            candidate.LastQuote = quote.Price;
            candidate.QuoteAt = quote.At.ToUniversalTime();
        }

        // Persist crossings before notifying. If the send then fails, the alert rows survive
        // with LastNotifiedAt untouched, so the next tick retries the email rather than
        // silently dropping it — the crossing itself can never recur once LastQuote moves.
        await db.SaveChangesAsync(ct);

        var pending = CollectPendingNotifications(monitored, now);
        if (pending.Count == 0)
        {
            return;
        }

        await notifier.SendAsync(AlertDigest.Build(pending.Select(p => p.Notification).ToList(),
            appOptions.Value.PublicBaseUrl), ct);

        foreach (var (alert, _) in pending)
        {
            alert.LastNotifiedAt = now;
        }

        run.NotificationsSent = pending.Count;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Alerts that are currently triggered, unacknowledged, and outside the notification
    /// cooldown. Derived from persisted state rather than from this tick's crossings so a
    /// failed send is retried, and so a condition that persists produces a periodic reminder.
    /// </summary>
    private List<(Alert Alert, AlertNotification Notification)> CollectPendingNotifications(
        List<Candidate> candidates, DateTimeOffset now)
    {
        var pending = new List<(Alert, AlertNotification)>();

        foreach (var candidate in candidates)
        {
            var levels = LevelEvaluator.LevelsFor(candidate).ToDictionary(l => l.Key);

            foreach (var alert in candidate.Alerts)
            {
                if (alert.LevelKey is null || !alert.Triggered || alert.AcknowledgedAt is not null)
                {
                    continue;
                }

                if (!IsOutsideCooldown(alert, now) ||
                    !levels.TryGetValue(alert.LevelKey, out var level))
                {
                    continue;
                }

                var price = alert.TriggerPrice ?? candidate.LastQuote ?? level.Value;
                pending.Add((
                    alert,
                    new AlertNotification(
                        candidate.Ticker, candidate.Company, new LevelCrossing(level, price, price))));
            }
        }

        return pending;
    }

    private void UpsertAlert(
        Candidate candidate, LevelCrossing crossing, decimal price, DateTimeOffset now)
    {
        var alert = candidate.Alerts.FirstOrDefault(a => a.LevelKey == crossing.Key);

        if (alert is null)
        {
            alert = new Alert
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                LevelKey = crossing.Key,
                AutoGenerated = true
            };

            // Add through the DbSet, not just the navigation collection: an untracked entity
            // reached via a navigation with its key already set is classified Modified, which
            // issues an UPDATE against a row that does not exist yet.
            db.Alerts.Add(alert);

            // Relationship fixup usually puts it in the navigation for us; only add it
            // ourselves if it did not, or this tick would process the alert twice.
            if (!candidate.Alerts.Contains(alert))
            {
                candidate.Alerts.Add(alert);
            }
        }

        alert.AlertType = crossing.Level.Type;
        alert.Active = true;
        alert.Triggered = true;
        alert.TriggeredAt = now;
        alert.TriggerPrice = price;
        alert.AcknowledgedAt = null;
        alert.LastChecked = now;

        // Mirror the watched level onto the row so the detail page shows what was crossed.
        (alert.LevelLow, alert.LevelHigh) = crossing.Key switch
        {
            LevelKeys.SupportZone =>
                (candidate.SupportLow, candidate.SupportHigh ?? candidate.SupportLow),
            LevelKeys.SecondarySupportZone =>
                (candidate.SecondarySupportLow, candidate.SecondarySupportHigh ?? candidate.SecondarySupportLow),
            _ => ((decimal?)crossing.Level.Value, (decimal?)null)
        };
    }

    /// <summary>
    /// Clears the triggered flag once price has moved clear of a level by the re-arm margin.
    /// This is what lets the same level fire again later, and what stops a ticker chopping
    /// around its trigger from re-firing every tick.
    /// </summary>
    private void ReArmClearedAlerts(Candidate candidate, decimal price, DateTimeOffset now)
    {
        var levels = LevelEvaluator.LevelsFor(candidate).ToDictionary(l => l.Key);

        foreach (var alert in candidate.Alerts.Where(a => a.LevelKey is not null))
        {
            alert.LastChecked = now;

            if (!alert.Triggered || !levels.TryGetValue(alert.LevelKey!, out var level))
            {
                continue;
            }

            if (LevelEvaluator.HasReArmed(level, price, _options.ReArmPercent))
            {
                alert.Triggered = false;
                alert.TriggeredAt = null;
                alert.TriggerPrice = null;
            }
        }
    }

    private bool IsOutsideCooldown(Alert alert, DateTimeOffset now) =>
        alert.LastNotifiedAt is not { } last
        || now - last >= TimeSpan.FromHours(_options.MinNotifyHours);
}

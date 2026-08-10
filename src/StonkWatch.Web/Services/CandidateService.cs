using Microsoft.EntityFrameworkCore;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;

namespace StonkWatch.Web.Services;

public class CandidateService(StonkWatchDbContext db, TimeProvider timeProvider)
{
    public async Task<List<CandidateDto>> ListAsync(
        CandidateStatus? status, Priority? priority, CancellationToken ct = default)
    {
        var query = db.Candidates.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(c => c.Status == status);
        }
        if (priority is not null)
        {
            query = query.Where(c => c.Priority == priority);
        }

        var candidates = await query.OrderBy(c => c.Ticker).ToListAsync(ct);
        return candidates.Select(ToDto).ToList();
    }

    public async Task<CandidateDetailDto?> GetByTickerAsync(string ticker, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var candidate = await db.Candidates.AsNoTracking()
            .Include(c => c.Alerts)
            .Include(c => c.ReviewLogs)
            .FirstOrDefaultAsync(c => c.Ticker == normalized, ct);
        if (candidate is null)
        {
            return null;
        }

        var alerts = candidate.Alerts
            .OrderByDescending(a => a.LastChecked)
            .Select(a => ToDto(a, candidate.Ticker))
            .ToList();
        var reviews = candidate.ReviewLogs
            .OrderByDescending(r => r.ReviewDate)
            .Select(ToDto)
            .ToList();

        return new CandidateDetailDto(ToDto(candidate), alerts, reviews);
    }

    public async Task<CandidateDto> CreateAsync(CreateCandidateRequest request, CancellationToken ct = default)
    {
        var ticker = Normalize(request.Ticker);
        if (string.IsNullOrWhiteSpace(ticker))
        {
            throw new ValidationException("Ticker is required.");
        }

        if (await db.Candidates.AnyAsync(c => c.Ticker == ticker, ct))
        {
            throw new ConflictException($"Candidate '{ticker}' already exists.");
        }

        var now = timeProvider.GetUtcNow();
        var candidate = new Candidate
        {
            Id = Guid.NewGuid(),
            Ticker = ticker,
            Company = request.Company,
            Exchange = request.Exchange,
            Currency = request.Currency,
            Priority = EnumParsing.ParseOrDefault(request.Priority, Priority.Medium),
            Status = EnumParsing.ParseOrDefault(request.Status, CandidateStatus.Idea),
            Conviction = EnumParsing.ParseNullableOrDefault<Conviction>(request.Conviction, null),
            PreferredSetup = request.PreferredSetup,
            Thesis = request.Thesis,
            CurrentPrice = request.CurrentPrice,
            SupportLow = request.SupportLow,
            SupportHigh = request.SupportHigh,
            SecondarySupportLow = request.SecondarySupportLow,
            SecondarySupportHigh = request.SecondarySupportHigh,
            ReclaimTrigger1 = request.ReclaimTrigger1,
            ReclaimTrigger2 = request.ReclaimTrigger2,
            Invalidation = request.Invalidation,
            T1 = request.T1,
            T2 = request.T2,
            NextEvent = request.NextEvent,
            EventDate = request.EventDate,
            DataQuality = EnumParsing.ParseOrDefault(request.DataQuality, DataQuality.Unavailable),
            MainRisk = request.MainRisk,
            SourceNotes = request.SourceNotes,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Candidates.Add(candidate);
        await db.SaveChangesAsync(ct);
        return ToDto(candidate);
    }

    public async Task<CandidateDto?> UpdateAsync(
        string ticker, UpdateCandidateRequest request, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var candidate = await db.Candidates.FirstOrDefaultAsync(c => c.Ticker == normalized, ct);
        if (candidate is null)
        {
            return null;
        }

        candidate.Company = MergeString(request.Company, candidate.Company);
        candidate.Exchange = MergeString(request.Exchange, candidate.Exchange);
        candidate.Currency = MergeString(request.Currency, candidate.Currency);
        candidate.Priority = EnumParsing.ParseOrDefault(request.Priority, candidate.Priority);
        candidate.Status = EnumParsing.ParseOrDefault(request.Status, candidate.Status);
        candidate.Conviction = EnumParsing.ParseNullableOrDefault(request.Conviction, candidate.Conviction);
        candidate.PreferredSetup = MergeString(request.PreferredSetup, candidate.PreferredSetup);
        candidate.Thesis = MergeString(request.Thesis, candidate.Thesis);
        candidate.CurrentPrice = request.CurrentPrice ?? candidate.CurrentPrice;
        candidate.SupportLow = request.SupportLow ?? candidate.SupportLow;
        candidate.SupportHigh = request.SupportHigh ?? candidate.SupportHigh;
        candidate.SecondarySupportLow = request.SecondarySupportLow ?? candidate.SecondarySupportLow;
        candidate.SecondarySupportHigh = request.SecondarySupportHigh ?? candidate.SecondarySupportHigh;
        candidate.ReclaimTrigger1 = request.ReclaimTrigger1 ?? candidate.ReclaimTrigger1;
        candidate.ReclaimTrigger2 = request.ReclaimTrigger2 ?? candidate.ReclaimTrigger2;
        candidate.Invalidation = request.Invalidation ?? candidate.Invalidation;
        candidate.T1 = request.T1 ?? candidate.T1;
        candidate.T2 = request.T2 ?? candidate.T2;
        candidate.NextEvent = MergeString(request.NextEvent, candidate.NextEvent);
        candidate.EventDate = request.EventDate ?? candidate.EventDate;
        candidate.DataQuality = EnumParsing.ParseOrDefault(request.DataQuality, candidate.DataQuality);
        candidate.MainRisk = MergeString(request.MainRisk, candidate.MainRisk);
        candidate.SourceNotes = MergeString(request.SourceNotes, candidate.SourceNotes);
        candidate.UpdatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return ToDto(candidate);
    }

    public async Task<bool> DeleteAsync(string ticker, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var candidate = await db.Candidates.FirstOrDefaultAsync(c => c.Ticker == normalized, ct);
        if (candidate is null)
        {
            return false;
        }

        db.Candidates.Remove(candidate);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ReviewLogDto?> LogReviewAsync(
        string ticker, LogReviewRequest request, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var candidate = await db.Candidates.FirstOrDefaultAsync(c => c.Ticker == normalized, ct);
        if (candidate is null)
        {
            return null;
        }

        // Npgsql only accepts UTC-normalized DateTimeOffset values for timestamptz columns.
        var reviewDate = (request.ReviewDate ?? timeProvider.GetUtcNow()).ToUniversalTime();
        var entry = new ReviewLogEntry
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            ReviewDate = reviewDate,
            Price = request.Price,
            StatusAtReview = EnumParsing.ParseNullableOrDefault<CandidateStatus>(request.StatusAtReview, null),
            ThesisImpact = EnumParsing.ParseNullableOrDefault<ThesisImpact>(request.ThesisImpact, null),
            WhatChanged = request.WhatChanged,
            LevelsChanged = request.LevelsChanged,
            NextAction = request.NextAction,
            Notes = request.Notes
        };
        db.ReviewLogs.Add(entry);

        candidate.LastReviewed = reviewDate;
        if (request.Price is not null)
        {
            candidate.ReviewedPrice = request.Price;
            candidate.CurrentPrice = request.Price;
        }
        candidate.UpdatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return ToDto(entry);
    }

    public async Task<List<AlertDto>> GetAlertsAsync(bool? triggeredOnly, CancellationToken ct = default)
    {
        var query = db.Alerts.AsNoTracking().Include(a => a.Candidate).AsQueryable();
        if (triggeredOnly == true)
        {
            query = query.Where(a => a.Triggered);
        }

        var alerts = await query.OrderByDescending(a => a.LastChecked).ToListAsync(ct);
        return alerts.Select(a => ToDto(a, a.Candidate!.Ticker)).ToList();
    }

    public async Task<AlertDto?> AddAlertAsync(
        string ticker, CreateAlertRequest request, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var candidate = await db.Candidates.FirstOrDefaultAsync(c => c.Ticker == normalized, ct);
        if (candidate is null)
        {
            return null;
        }

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            AlertType = EnumParsing.ParseOrDefault(request.AlertType, AlertType.PrimarySupport),
            LevelLow = request.LevelLow,
            LevelHigh = request.LevelHigh,
            ConditionSignal = request.ConditionSignal,
            Active = request.Active,
            Triggered = false,
            LastChecked = timeProvider.GetUtcNow()
        };

        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);
        return ToDto(alert, candidate.Ticker);
    }

    public async Task<AlertDto?> UpdateAlertAsync(
        string ticker, Guid alertId, UpdateAlertRequest request, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var alert = await db.Alerts.Include(a => a.Candidate)
            .FirstOrDefaultAsync(a => a.Id == alertId && a.Candidate!.Ticker == normalized, ct);
        if (alert is null)
        {
            return null;
        }

        alert.Active = request.Active ?? alert.Active;
        alert.Triggered = request.Triggered ?? alert.Triggered;
        alert.ConditionSignal = MergeString(request.ConditionSignal, alert.ConditionSignal);
        alert.LevelLow = request.LevelLow ?? alert.LevelLow;
        alert.LevelHigh = request.LevelHigh ?? alert.LevelHigh;
        alert.LastChecked = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return ToDto(alert, alert.Candidate!.Ticker);
    }

    /// <summary>
    /// Marks a triggered alert as seen so it stops being reported and stops re-notifying.
    /// The price-check worker clears this again if the level re-arms and fires afresh.
    /// </summary>
    public async Task<AlertDto?> AcknowledgeAlertAsync(
        string ticker, Guid alertId, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var alert = await db.Alerts.Include(a => a.Candidate)
            .FirstOrDefaultAsync(a => a.Id == alertId && a.Candidate!.Ticker == normalized, ct);
        if (alert is null)
        {
            return null;
        }

        alert.AcknowledgedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDto(alert, alert.Candidate!.Ticker);
    }

    /// <summary>Most recent run of a background job, for the dashboard's health strip.</summary>
    public async Task<JobRunDto?> GetLastJobRunAsync(string job, CancellationToken ct = default)
    {
        var run = await db.JobRuns.AsNoTracking()
            .Where(r => r.Job == job)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        return run is null ? null : ToDto(run);
    }

    public async Task<bool> DeleteAlertAsync(string ticker, Guid alertId, CancellationToken ct = default)
    {
        var normalized = Normalize(ticker);
        var alert = await db.Alerts.Include(a => a.Candidate)
            .FirstOrDefaultAsync(a => a.Id == alertId && a.Candidate!.Ticker == normalized, ct);
        if (alert is null)
        {
            return false;
        }

        db.Alerts.Remove(alert);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string? MergeString(string? incoming, string? current) => incoming switch
    {
        null => current,
        "" => null,
        _ => incoming
    };

    private static string Normalize(string ticker) => ticker.Trim().ToUpperInvariant();

    private static CandidateDto ToDto(Candidate c) => new(
        c.Id, c.Ticker, c.Company, c.Exchange, c.Currency,
        c.Priority.ToString(), c.Status.ToString(), c.Conviction?.ToString(),
        c.PreferredSetup, c.Thesis, c.CurrentPrice, c.ReviewedPrice, c.LastReviewed,
        c.SupportLow, c.SupportHigh, c.SecondarySupportLow, c.SecondarySupportHigh,
        c.ReclaimTrigger1, c.ReclaimTrigger2, c.Invalidation, c.T1, c.T2,
        c.NextEvent, c.EventDate, c.DataQuality.ToString(), c.MainRisk, c.SourceNotes,
        c.CreatedAt, c.UpdatedAt, c.LastQuote, c.QuoteAt);

    private static AlertDto ToDto(Alert a, string ticker) => new(
        a.Id, a.CandidateId, ticker, a.AlertType.ToString(), a.LevelLow, a.LevelHigh,
        a.ConditionSignal, a.Active, a.Triggered, a.LastChecked,
        a.LevelKey, a.TriggeredAt, a.TriggerPrice, a.AcknowledgedAt, a.AutoGenerated);

    private static JobRunDto ToDto(JobRun r) => new(
        r.Id, r.Job, r.StartedAt, r.FinishedAt, r.Status.ToString(),
        r.CandidatesChecked, r.AlertsFired, r.NotificationsSent, r.Error, r.SkipReason);

    private static ReviewLogDto ToDto(ReviewLogEntry r) => new(
        r.Id, r.CandidateId, r.ReviewDate, r.Price, r.StatusAtReview?.ToString(),
        r.ThesisImpact?.ToString(), r.WhatChanged, r.LevelsChanged, r.NextAction, r.Notes);
}

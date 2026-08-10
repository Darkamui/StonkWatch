using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Mcp;

[McpServerToolType]
public class WatchlistTools(CandidateService service)
{
    [McpServerTool(Name = "add_candidate")]
    [Description("Add a new swing-trade candidate to the watchlist. Only ticker is required; " +
                 "everything else can be filled in later. Enum-like fields (priority, status, " +
                 "conviction, data_quality) accept loose natural-language casing/spacing.")]
    public async Task<CandidateDto> AddCandidate(
        [Description("Ticker symbol, e.g. ASTS")] string ticker,
        [Description("Company name")] string? company = null,
        [Description("Exchange, e.g. Nasdaq, TSX")] string? exchange = null,
        [Description("Currency, e.g. USD, CAD")] string? currency = null,
        [Description("High, Medium, or Low")] string? priority = null,
        [Description("IDEA, WATCH, NEAR TRIGGER, REANALYZE, READY, INVALIDATED, or ENTERED")] string? status = null,
        [Description("Conviction grade: A, B, or C")] string? conviction = null,
        [Description("Preferred setup, e.g. 'Failed-breakdown reversal'")] string? setup = null,
        [Description("Short freeform thesis")] string? thesis = null,
        [Description("Current price")] decimal? currentPrice = null,
        [Description("Primary support zone low")] decimal? supportLow = null,
        [Description("Primary support zone high")] decimal? supportHigh = null,
        [Description("Secondary support zone low")] decimal? secondarySupportLow = null,
        [Description("Secondary support zone high")] decimal? secondarySupportHigh = null,
        [Description("First reclaim trigger price")] decimal? reclaimTrigger1 = null,
        [Description("Second reclaim trigger price")] decimal? reclaimTrigger2 = null,
        [Description("Invalidation price")] decimal? invalidation = null,
        [Description("Target 1 price")] decimal? t1 = null,
        [Description("Target 2 price")] decimal? t2 = null,
        [Description("Description of the next catalyst/event")] string? nextEvent = null,
        [Description("Date of next event, format yyyy-MM-dd")] string? eventDate = null,
        [Description("COMPLETE, PARTIAL, or UNAVAILABLE")] string? dataQuality = null,
        [Description("Main risk to the thesis")] string? mainRisk = null,
        [Description("Freeform source notes")] string? sourceNotes = null,
        CancellationToken cancellationToken = default)
    {
        return await Guarded(() =>
        {
            var request = new CreateCandidateRequest(
                ticker, company, exchange, currency, priority, status, conviction, setup, thesis,
                currentPrice, supportLow, supportHigh, secondarySupportLow, secondarySupportHigh,
                reclaimTrigger1, reclaimTrigger2, invalidation, t1, t2,
                nextEvent, ParseDate(eventDate), dataQuality, mainRisk, sourceNotes);

            return service.CreateAsync(request, cancellationToken);
        });
    }

    [McpServerTool(Name = "update_candidate")]
    [Description("Update fields on an existing watchlist candidate. Omit any field to leave it unchanged; " +
                 "pass an empty string for a text field to clear it.")]
    public async Task<CandidateDto> UpdateCandidate(
        [Description("Ticker symbol of the candidate to update")] string ticker,
        [Description("Company name")] string? company = null,
        [Description("Exchange, e.g. Nasdaq, TSX")] string? exchange = null,
        [Description("Currency, e.g. USD, CAD")] string? currency = null,
        [Description("High, Medium, or Low")] string? priority = null,
        [Description("IDEA, WATCH, NEAR TRIGGER, REANALYZE, READY, INVALIDATED, or ENTERED")] string? status = null,
        [Description("Conviction grade: A, B, or C")] string? conviction = null,
        [Description("Preferred setup, e.g. 'Failed-breakdown reversal'")] string? setup = null,
        [Description("Short freeform thesis")] string? thesis = null,
        [Description("Current price")] decimal? currentPrice = null,
        [Description("Primary support zone low")] decimal? supportLow = null,
        [Description("Primary support zone high")] decimal? supportHigh = null,
        [Description("Secondary support zone low")] decimal? secondarySupportLow = null,
        [Description("Secondary support zone high")] decimal? secondarySupportHigh = null,
        [Description("First reclaim trigger price")] decimal? reclaimTrigger1 = null,
        [Description("Second reclaim trigger price")] decimal? reclaimTrigger2 = null,
        [Description("Invalidation price")] decimal? invalidation = null,
        [Description("Target 1 price")] decimal? t1 = null,
        [Description("Target 2 price")] decimal? t2 = null,
        [Description("Description of the next catalyst/event")] string? nextEvent = null,
        [Description("Date of next event, format yyyy-MM-dd")] string? eventDate = null,
        [Description("COMPLETE, PARTIAL, or UNAVAILABLE")] string? dataQuality = null,
        [Description("Main risk to the thesis")] string? mainRisk = null,
        [Description("Freeform source notes")] string? sourceNotes = null,
        CancellationToken cancellationToken = default)
    {
        return await Guarded(async () =>
        {
            var request = new UpdateCandidateRequest(
                company, exchange, currency, priority, status, conviction, setup, thesis,
                currentPrice, supportLow, supportHigh, secondarySupportLow, secondarySupportHigh,
                reclaimTrigger1, reclaimTrigger2, invalidation, t1, t2,
                nextEvent, ParseDate(eventDate), dataQuality, mainRisk, sourceNotes);

            var updated = await service.UpdateAsync(ticker, request, cancellationToken);
            return updated ?? throw new ValidationException($"No candidate found for ticker '{ticker}'.");
        });
    }

    [McpServerTool(Name = "list_watchlist")]
    [Description("List watchlist candidates, optionally filtered by status and/or priority.")]
    public async Task<List<CandidateDto>> ListWatchlist(
        [Description("Filter by status: IDEA, WATCH, NEAR TRIGGER, REANALYZE, READY, INVALIDATED, ENTERED")] string? status = null,
        [Description("Filter by priority: High, Medium, Low")] string? priority = null,
        CancellationToken cancellationToken = default)
    {
        return await Guarded(() =>
        {
            var statusFilter = status is null ? (CandidateStatus?)null : EnumParsing.ParseOrDefault<CandidateStatus>(status, default);
            var priorityFilter = priority is null ? (Priority?)null : EnumParsing.ParseOrDefault<Priority>(priority, default);
            return service.ListAsync(statusFilter, priorityFilter, cancellationToken);
        });
    }

    [McpServerTool(Name = "log_review")]
    [Description("Log a review of a candidate: records a review_log entry and updates the candidate's " +
                 "last_reviewed date. If price is given it also updates current_price and reviewed_price.")]
    public async Task<ReviewLogDto> LogReview(
        [Description("Ticker symbol of the candidate reviewed")] string ticker,
        [Description("Price at time of review")] decimal? price = null,
        [Description("Improved, Unchanged, Weakened, or Invalidated")] string? thesisImpact = null,
        [Description("What changed since the last review")] string? whatChanged = null,
        [Description("Recommended next action")] string? nextAction = null,
        [Description("Status to record for this review (does not change the candidate's current status)")] string? statusAtReview = null,
        [Description("Whether support/trigger/target levels changed as part of this review")] bool levelsChanged = false,
        [Description("Freeform notes")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var request = new LogReviewRequest(price, statusAtReview, thesisImpact, whatChanged, levelsChanged, nextAction, notes);
        return await Guarded(async () =>
        {
            var entry = await service.LogReviewAsync(ticker, request, cancellationToken);
            return entry ?? throw new ValidationException($"No candidate found for ticker '{ticker}'.");
        });
    }

    [McpServerTool(Name = "get_alerts")]
    [Description("Get price-level alerts across the watchlist. Set triggeredOnly to true to see only alerts needing attention.")]
    public async Task<List<AlertDto>> GetAlerts(
        [Description("If true, only return alerts currently marked as triggered")] bool triggeredOnly = false,
        CancellationToken cancellationToken = default)
    {
        return await service.GetAlertsAsync(triggeredOnly ? true : null, cancellationToken);
    }

    /// <summary>
    /// Domain validation/conflict errors are safe, user-facing messages, but the MCP SDK replaces
    /// unrecognized exception types with a generic "an error occurred" message before returning them
    /// to the client. Re-throwing as McpException preserves our message for the caller.
    /// </summary>
    private static async Task<T> Guarded<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is ValidationException or ConflictException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ValidationException($"Invalid date '{raw}'. Expected format yyyy-MM-dd.");
        }

        return date;
    }
}

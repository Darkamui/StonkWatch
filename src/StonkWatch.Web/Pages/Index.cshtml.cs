using Microsoft.AspNetCore.Mvc.RazorPages;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Pages;

public class IndexModel(CandidateService service) : PageModel
{
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(14);

    public List<CandidateDto> Candidates { get; private set; } = [];
    public Dictionary<string, int> StatusCounts { get; private set; } = [];
    public List<CandidateDto> StaleCandidates { get; private set; } = [];
    public List<AlertDto> TriggeredAlerts { get; private set; } = [];
    public JobRunDto? LastPriceCheck { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        LastPriceCheck = await service.GetLastJobRunAsync(JobNames.PriceCheck, ct);

        Candidates = await service.ListAsync(null, null, ct);

        StatusCounts = Enum.GetNames<CandidateStatus>()
            .ToDictionary(name => name, name => Candidates.Count(c => c.Status == name));

        var cutoff = DateTimeOffset.UtcNow - StaleThreshold;
        StaleCandidates = Candidates
            .Where(c => c.Status is not (nameof(CandidateStatus.Invalidated) or nameof(CandidateStatus.Entered)))
            .Where(c => c.LastReviewed is null || c.LastReviewed < cutoff)
            .OrderBy(c => c.LastReviewed ?? DateTimeOffset.MinValue)
            .ToList();

        // Acknowledged alerts have been dealt with; keep them off the dashboard.
        TriggeredAlerts = (await service.GetAlertsAsync(true, ct))
            .Where(a => a.AcknowledgedAt is null)
            .ToList();
    }
}

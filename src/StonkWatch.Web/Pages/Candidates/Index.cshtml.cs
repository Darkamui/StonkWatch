using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Pages.Candidates;

public class IndexModel(CandidateService service) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Priority { get; set; }

    public List<CandidateDto> Candidates { get; private set; } = [];
    public string? FilterError { get; private set; }

    /// <summary>Unacknowledged triggered alerts, keyed by ticker, for the row indicators.</summary>
    public Dictionary<string, List<AlertDto>> TriggeredAlerts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        CandidateStatus? statusFilter = null;
        Priority? priorityFilter = null;

        try
        {
            statusFilter = string.IsNullOrEmpty(Status) ? null : EnumParsing.ParseOrDefault<CandidateStatus>(Status, default);
            priorityFilter = string.IsNullOrEmpty(Priority) ? null : EnumParsing.ParseOrDefault<Priority>(Priority, default);
        }
        catch (ValidationException ex)
        {
            FilterError = ex.Message;
        }

        Candidates = await service.ListAsync(statusFilter, priorityFilter, ct);

        TriggeredAlerts = (await service.GetAlertsAsync(true, ct))
            .Where(a => a.AcknowledgedAt is null)
            .GroupBy(a => a.Ticker)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(
        string ticker, string newStatus, string? status, string? priority, CancellationToken ct)
    {
        await service.UpdateAsync(ticker, new UpdateCandidateRequest(Status: newStatus), ct);
        TempData["Flash"] = $"{ticker} status set to {newStatus}.";
        return RedirectToPage(new { status, priority });
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        string ticker, string? status, string? priority, CancellationToken ct)
    {
        await service.DeleteAsync(ticker, ct);
        TempData["Flash"] = $"{ticker} deleted.";
        return RedirectToPage(new { status, priority });
    }
}

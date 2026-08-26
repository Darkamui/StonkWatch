using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Pages.Candidates;

public class DetailModel(CandidateService service) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PrettyPrintOptions = new() { WriteIndented = true };

    [BindProperty(SupportsGet = true)]
    public string Ticker { get; set; } = "";

    [BindProperty]
    public string Json { get; set; } = "";

    [BindProperty]
    public LogReviewInput ReviewInput { get; set; } = new();

    [BindProperty]
    public AlertInput NewAlert { get; set; } = new();

    public CandidateDetailDto? Detail { get; private set; }
    public string? ErrorMessage { get; set; }

    /// <summary>True when the JSON editor should render open instead of the read-only view — only after a failed edit attempt, so the user's edits and the error stay visible.</summary>
    public bool ShowJsonEditor { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Detail = await service.GetByTickerAsync(Ticker, ct);
        if (Detail is null)
        {
            return NotFound();
        }

        Json = JsonSerializer.Serialize(CandidateJsonInput.FromDto(Detail.Candidate), PrettyPrintOptions);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateJsonAsync(CancellationToken ct)
    {
        ShowJsonEditor = true;

        CandidateJsonInput input;
        try
        {
            input = JsonSerializer.Deserialize<CandidateJsonInput>(Json, JsonOptions)
                ?? throw new JsonException("Empty JSON.");
        }
        catch (JsonException ex)
        {
            ErrorMessage = $"Couldn't parse that JSON: {ex.Message}";
            Detail = await service.GetByTickerAsync(Ticker, ct);
            return Page();
        }

        try
        {
            var updated = await service.UpdateWithHistoryAsync(Ticker, input.ToUpdateRequest(), ct);
            if (updated is null)
            {
                return NotFound();
            }

            TempData["Flash"] = "Saved.";
            return RedirectToPage(new { ticker = Ticker });
        }
        catch (ValidationException ex)
        {
            ErrorMessage = ex.Message;
            Detail = await service.GetByTickerAsync(Ticker, ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        await service.DeleteAsync(Ticker, ct);
        TempData["Flash"] = $"{Ticker} deleted.";
        return RedirectToPage("/Candidates/Index");
    }

    public async Task<IActionResult> OnPostLogReviewAsync(CancellationToken ct)
    {
        var request = new LogReviewRequest(
            ReviewInput.Price, ReviewInput.StatusAtReview, ReviewInput.ThesisImpact,
            ReviewInput.WhatChanged, ReviewInput.LevelsChanged, ReviewInput.NextAction, ReviewInput.Notes);

        var entry = await service.LogReviewAsync(Ticker, request, ct);
        if (entry is null)
        {
            return NotFound();
        }

        TempData["Flash"] = "Review logged.";
        return RedirectToPage(new { ticker = Ticker });
    }

    public async Task<IActionResult> OnPostAddAlertAsync(CancellationToken ct)
    {
        var request = new CreateAlertRequest(
            NewAlert.AlertType, NewAlert.LevelLow, NewAlert.LevelHigh, NewAlert.ConditionSignal, NewAlert.Active);

        var alert = await service.AddAlertAsync(Ticker, request, ct);
        if (alert is null)
        {
            return NotFound();
        }

        TempData["Flash"] = "Alert added.";
        return RedirectToPage(new { ticker = Ticker });
    }

    public async Task<IActionResult> OnPostToggleAlertAsync(Guid alertId, bool active, bool triggered, CancellationToken ct)
    {
        await service.UpdateAlertAsync(Ticker, alertId, new UpdateAlertRequest(active, triggered), ct);
        return RedirectToPage(new { ticker = Ticker });
    }

    public async Task<IActionResult> OnPostAcknowledgeAlertAsync(Guid alertId, CancellationToken ct)
    {
        await service.AcknowledgeAlertAsync(Ticker, alertId, ct);
        TempData["Flash"] = "Alert acknowledged.";
        return RedirectToPage(new { ticker = Ticker });
    }

    public async Task<IActionResult> OnPostDeleteAlertAsync(Guid alertId, CancellationToken ct)
    {
        await service.DeleteAlertAsync(Ticker, alertId, ct);
        TempData["Flash"] = "Alert removed.";
        return RedirectToPage(new { ticker = Ticker });
    }
}

public class LogReviewInput
{
    public decimal? Price { get; set; }
    public string? StatusAtReview { get; set; }
    public string? ThesisImpact { get; set; }
    public string? WhatChanged { get; set; }
    public bool LevelsChanged { get; set; }
    public string? NextAction { get; set; }
    public string? Notes { get; set; }
}

public class AlertInput
{
    public string AlertType { get; set; } = "PrimarySupport";
    public decimal? LevelLow { get; set; }
    public decimal? LevelHigh { get; set; }
    public string? ConditionSignal { get; set; }
    public bool Active { get; set; } = true;
}

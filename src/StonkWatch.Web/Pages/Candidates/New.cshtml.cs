using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Pages.Candidates;

public class NewModel(CandidateService service) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [BindProperty]
    public string Json { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        CandidateJsonInput input;
        try
        {
            input = JsonSerializer.Deserialize<CandidateJsonInput>(Json, JsonOptions)
                ?? throw new JsonException("Empty JSON.");
        }
        catch (JsonException ex)
        {
            ErrorMessage = $"Couldn't parse that JSON: {ex.Message}";
            return Page();
        }

        try
        {
            var created = await service.CreateAsync(input.ToCreateRequest(), ct);
            TempData["Flash"] = $"{created.Ticker} added.";
            return RedirectToPage("/Candidates/Detail", new { ticker = created.Ticker });
        }
        catch (ValidationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
        catch (ConflictException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}

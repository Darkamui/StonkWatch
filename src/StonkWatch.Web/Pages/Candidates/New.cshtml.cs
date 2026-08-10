using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Pages.Candidates;

public class NewModel(CandidateService service) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(20)]
    public string Ticker { get; set; } = "";

    [BindProperty]
    public CandidateFormInput Form { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var created = await service.CreateAsync(Form.ToCreateRequest(Ticker), ct);
            TempData["Flash"] = $"{created.Ticker} added.";
            return RedirectToPage("/Candidates/Detail", new { ticker = created.Ticker });
        }
        catch (Data.ValidationException ex)
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

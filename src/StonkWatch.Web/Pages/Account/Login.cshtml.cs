using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StonkWatch.Web.Pages.Account;

public class LoginModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Error { get; set; }

    public string? ErrorMessage => Error switch
    {
        "access_denied" => "That Google account isn't authorized for StonkWatch.",
        _ => null,
    };

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/",
        };

        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }
}

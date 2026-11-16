using MatPhoto.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPhoto.Pages;

/// <summary>The start page: for a visitor what MatPhoto is, for a signed-in person straight to the photos.</summary>
public sealed class IndexModel(AccountService accounts, CurrentUser me) : PageModel
{
    public bool RegistrationOpen { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (me.IsAuthenticated)
        {
            return LocalRedirect("/Photos");
        }

        RegistrationOpen = !await accounts.AnyUserAsync() || (await accounts.SettingsAsync()).RegistrationOpen;
        return Page();
    }
}

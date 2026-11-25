using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Pages.Link;

/// <summary>
/// The page of a link (<c>/s/{token}</c>) for anybody who has it, with or without an account: the title, who shared it, the grid and the viewer of the album or the
/// list (the scripts read from <c>/s/{token}/…</c>). A link that does not open answers 404 like an address that never existed.
/// </summary>
public sealed class IndexModel(AppDbContext db, LibrarySettings settings, CurrentUser me) : PageModel
{
    public SharedView View { get; private set; } = null!;

    public int Count { get; private set; }

    public bool IsList => View.Scope.IsList;

    public async Task<IActionResult> OnGetAsync(string token)
    {
        var ct = HttpContext.RequestAborted;
        if (await ShareService.OpenAsync(db, token, settings.Current.SharingEnabled, ct) is not { } view)
        {
            return NotFound();
        }

        View = view;
        Count = await (await LibraryService.QueryAsync(db, view.OwnerId, view.Scope, ct))!.CountAsync(ct);

        // a visit of the person who made the link is not counted
        if (me.Id != view.OwnerId)
        {
            await ShareService.CountVisitAsync(db, view.ShareId, ct);
        }

        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return Page();
    }

    public string Sub => Text.T("Share.Sub", Text.T("Explore.Count", Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)), View.OwnerName)
        + (View.ExpiresDate is { } last ? " · " + Text.T("Share.Until", Dates.Long(last, withYear: true)) : string.Empty);
}

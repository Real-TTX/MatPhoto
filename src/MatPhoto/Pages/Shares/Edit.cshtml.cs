using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace MatPhoto.Pages.Shares;

/// <summary>
/// A link for people without an account: made from the page of an album (<c>?album=</c>) or a list (<c>?list=</c>), changed by its id (<c>?id=</c>). The page shows
/// the address to copy, the title and the words the visitors see, the last day and whether they may download the originals. Delete withdraws the link at once; an
/// administrator may withdraw anybody's link (and only that).
/// </summary>
public sealed class EditModel(AppDbContext db, LibrarySettings settings, IMemoryCache memory) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty(SupportsGet = true)] public long? Album { get; set; }

    [BindProperty(SupportsGet = true)] public long? List { get; set; }

    [BindProperty] public string Title { get; set; } = string.Empty;

    [BindProperty] public string Message { get; set; } = string.Empty;

    [BindProperty] public string ExpiresDate { get; set; } = string.Empty;

    [BindProperty] public bool AllowDownload { get; set; }

    public bool IsNew => Id is null;

    /// <summary>The link of somebody else (an administrator looks at it): it can only be withdrawn.</summary>
    public bool ReadOnly { get; private set; }

    public ShareTarget Target { get; private set; } = null!;

    public ShareDetail? Share { get; private set; }

    public string Heading { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    /// <summary>The page of the album or the list (Back goes there when the link is new).</summary>
    public string TargetUrl => (Target.IsAlbum ? "/Albums/View/" : "/Lists/View/") + Target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string MinDate => Dates.Iso(Today);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Share is { } s)
        {
            Title = s.Title ?? string.Empty;
            Message = s.Message ?? string.Empty;
            ExpiresDate = s.ExpiresDate is { } d ? Dates.Iso(d) : string.Empty;
            AllowDownload = s.AllowDownload;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync() || ReadOnly)
        {
            return NotFound();
        }

        var expires = Dates.ParseIso(ExpiresDate);
        if (!string.IsNullOrWhiteSpace(ExpiresDate) && expires is null)
        {
            ModelState.AddModelError(nameof(ExpiresDate), Text.T("Shares.Error.Date"));
            return Page();
        }

        var input = new ShareInput(Title, Message, expires, AllowDownload);
        var (saved, errors) = Share is { } s
            ? await ShareService.SaveAsync(db, UserId, s.Id, input, Today, HttpContext.RequestAborted)
            : await ShareService.CreateAsync(db, UserId, Target, input, Today, HttpContext.RequestAborted);
        if (saved is { } id)
        {
            if (Share is { } before)
            {
                LibraryEndpoints.ForgetShare(memory, before.Token);
            }

            Flash(Text.T(Share is null ? "Shares.Created.Flash" : "Common.Saved"));
            return LocalRedirect($"/Shares/Edit?id={id}");
        }

        if (errors.Count == 0)
        {
            return NotFound();
        }

        errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (await ShareService.DeleteAsync(db, UserId, IsAdmin, Id ?? 0, HttpContext.RequestAborted) is not { } token)
        {
            return NotFound();
        }

        LibraryEndpoints.ForgetShare(memory, token);
        Flash(Text.T("Shares.Withdrawn"));
        return LocalRedirect("/Shares");
    }

    /// <summary>The link (by its id) or the album or list a new one is for; false when there is none the person may use.</summary>
    private async Task<bool> LoadAsync()
    {
        if (!settings.Current.SharingEnabled)
        {
            return false;
        }

        var ct = HttpContext.RequestAborted;
        if (Id is { } id)
        {
            Share = await ShareService.GetAsync(db, UserId, IsAdmin, id, ct);
            if (Share is null)
            {
                return false;
            }

            Target = Share.Target;
            ReadOnly = !Share.IsMine;
            Heading = Share.Title ?? Share.Target.Name;
            Address = $"{await OriginAsync()}/s/{Share.Token}";
            return true;
        }

        if (await ShareService.TargetAsync(db, UserId, Album, Album is null ? List : null, ct) is not { } target)
        {
            return false;
        }

        Target = target;
        Heading = Text.T("Shares.NewFor", target.Name);
        return true;
    }
}

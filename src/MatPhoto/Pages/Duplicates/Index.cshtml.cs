using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Duplicates;

/// <summary>
/// The duplicates of the library: the same file twice (exact) and pictures that look the same (similar). Per group the person keeps one (the largest is suggested)
/// and moves the others into the trash folder of their storage, or says they are no duplicates (not offered again).
/// </summary>
public sealed class IndexModel(AppDbContext db, StorageConnections connections, LibrarySettings settings) : AppPageModel
{
    public static readonly string[] Kinds = ["all", "exact", "similar"];

    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "all";

    [BindProperty] public long[] Ids { get; set; } = [];

    [BindProperty] public long Keep { get; set; }

    public DuplicateReport Report { get; private set; } = new([], 0, 0, 0);

    public async Task OnGetAsync()
    {
        Kind = Kinds.Contains(Kind) ? Kind : "all";
        Report = await DuplicateService.FindAsync(db, UserId, settings.Current.SimilarThreshold, Kind, HttpContext.RequestAborted);
    }

    /// <summary>Keeps one photo of a group and moves the others into the trash folder.</summary>
    public async Task<IActionResult> OnPostResolveAsync()
    {
        var others = Ids.Where(id => id != Keep).Distinct().ToList();
        if (others.Count == 0 || !Ids.Contains(Keep))
        {
            return LocalRedirect(Back());
        }

        var result = await MediaActions.TrashAsync(db, connections, UserId, others, HttpContext.RequestAborted);
        if (result.Errors.Count > 0 || result.Refused > 0)
        {
            FlashError(string.Join(" ", new[] { result.Refused > 0 ? Text.T("Trash.Refused", result.Refused) : null }.Concat(result.Errors).Where(s => s is not null)));
        }
        else
        {
            Flash(Text.T("Trash.Moved", result.Moved));
        }

        return LocalRedirect(Back());
    }

    /// <summary>The photos of a group are not duplicates.</summary>
    public async Task<IActionResult> OnPostDismissAsync()
    {
        await DuplicateService.DismissAsync(db, UserId, Ids, HttpContext.RequestAborted);
        Flash(Text.T("Duplicates.Dismissed"));
        return LocalRedirect(Back());
    }

    private string Back() => "/Duplicates" + (Kind is "exact" or "similar" ? "?kind=" + Kind : string.Empty);
}

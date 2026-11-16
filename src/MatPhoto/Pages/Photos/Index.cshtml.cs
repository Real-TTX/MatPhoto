using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;

namespace MatPhoto.Pages.Photos;

/// <summary>The timeline: every photo and video of the storages the person may see, newest first, month by month (the grid loads itself).</summary>
public sealed class IndexModel(AppDbContext db, MenuService menus) : AppPageModel
{
    public string Sub { get; private set; } = string.Empty;

    public int Pending { get; private set; }

    public bool HasStorages { get; private set; }

    public bool CanImport { get; private set; }

    public async Task OnGetAsync()
    {
        var menu = await menus.GetAsync();
        HasStorages = menu.HasStorages;
        CanImport = menu.CanImport;
        var (photos, videos, pending) = await LibraryService.CountsAsync(db, UserId, HttpContext.RequestAborted);
        Pending = pending;
        Sub = Text.T("Photos.Sub", photos.ToString("N0", System.Globalization.CultureInfo.CurrentUICulture), videos.ToString("N0", System.Globalization.CultureInfo.CurrentUICulture));
    }
}

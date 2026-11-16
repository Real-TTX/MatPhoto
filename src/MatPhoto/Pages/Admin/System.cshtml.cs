using MatPhoto.Data;
using MatPhoto.Controls;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Mail;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Pages.Admin;

/// <summary>
/// The settings of the installation in three tabs: General (registration, public address, the largest upload), Thumbnails (where the cache lies – the data volume or a
/// folder of a storage, e.g. the NAS – and the sizes and quality), Import (the date in album names, when a new album starts, the target, similar photos). Beside it what
/// is in the installation. The same values as the platform API; an address <c>MAT_PUBLIC_URL</c> sets is shown read-only.
/// </summary>
public sealed class SystemModel(InstanceService instance, MailService mail, AppDbContext db) : AppPageModel
{
    private const long Mb = 1024L * 1024;

    public static readonly string[] Tabs = ["general", "thumbnails", "import"];

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "general";

    [BindProperty] public bool RegistrationOpen { get; set; }

    [BindProperty] public string PublicUrl { get; set; } = string.Empty;

    [BindProperty] public long MaxUploadMb { get; set; }

    [BindProperty] public string CacheStorageId { get; set; } = string.Empty;

    [BindProperty] public string CacheFolder { get; set; } = ".matphoto-cache";

    [BindProperty] public int ThumbnailHeight { get; set; } = 480;

    [BindProperty] public int PreviewSize { get; set; } = 2048;

    [BindProperty] public int ThumbnailQuality { get; set; } = 82;

    [BindProperty] public string AlbumDateFormat { get; set; } = "yyyy.MM.dd";

    [BindProperty] public int ImportGapHours { get; set; } = 8;

    [BindProperty] public int ImportDistanceKm { get; set; } = 50;

    [BindProperty] public string ImportStorageId { get; set; } = string.Empty;

    [BindProperty] public string ImportFolder { get; set; } = string.Empty;

    [BindProperty] public int SimilarThreshold { get; set; } = 6;

    /// <summary><c>MAT_PUBLIC_URL</c> sets the public address.</summary>
    public bool PublicUrlFixed { get; private set; }

    public InstanceSnapshot Counts { get; private set; } = null!;

    public string Database { get; private set; } = string.Empty;

    public long FreeBytes { get; private set; }

    public List<Choice> CacheChoices { get; private set; } = [];

    public List<Choice> ImportChoices { get; private set; } = [];

    /// <summary>Where the mail goes (MatSMTP), or null when the installation sends none.</summary>
    public string? MailText => mail.Describe(Languages.Current);

    /// <summary>An example of a new album name with the format of the form.</summary>
    public string Example => Services.Library.AlbumNames.Compose(new DateOnly(2023, 11, 9), "Hochzeit Julius", AlbumDateFormat);

    public async Task OnGetAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";
        var s = await instance.SettingsAsync();
        RegistrationOpen = s.RegistrationOpen;
        PublicUrl = s.PublicUrl ?? string.Empty;
        MaxUploadMb = s.MaxUploadBytes / Mb;
        CacheStorageId = s.CacheStorageId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CacheFolder = s.CacheFolder;
        ThumbnailHeight = s.ThumbnailHeight;
        PreviewSize = s.PreviewSize;
        ThumbnailQuality = s.ThumbnailQuality;
        AlbumDateFormat = s.AlbumDateFormat;
        ImportGapHours = s.ImportGapHours;
        ImportDistanceKm = s.ImportDistanceKm;
        ImportStorageId = s.ImportStorageId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        ImportFolder = s.ImportFolder;
        SimilarThreshold = s.SimilarThreshold;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";

        // each tab sends its own fields; the others stay as they are
        var change = Tab switch
        {
            "thumbnails" => new SettingsChange(CacheStorageId: long.TryParse(CacheStorageId, out var c) ? c : 0, CacheFolder: CacheFolder, ThumbnailHeight: ThumbnailHeight,
                PreviewSize: PreviewSize, ThumbnailQuality: ThumbnailQuality),
            "import" => new SettingsChange(AlbumDateFormat: AlbumDateFormat.Trim(), ImportGapHours: ImportGapHours, ImportDistanceKm: ImportDistanceKm,
                ImportStorageId: long.TryParse(ImportStorageId, out var i) ? i : 0, ImportFolder: ImportFolder, SimilarThreshold: SimilarThreshold),
            _ => new SettingsChange(RegistrationOpen: RegistrationOpen, PublicUrl: PublicUrl, MaxUploadBytes: Math.Max(0, MaxUploadMb) * Mb),
        };
        if (await instance.SaveSettingsAsync(change) is { } problem)
        {
            ModelState.AddModelError(problem.Field, problem.Message);
            await LoadAsync();
            return Page();
        }

        Services.Library.ActivityLog.Add(db, UserId, "setting", Text.T("System.Tab." + Tab));
        await db.SaveChangesAsync();
        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/System?tab=" + Tab);
    }

    public async Task<IActionResult> OnPostCleanUpAsync()
    {
        var result = await instance.CleanUpAsync(HttpContext.RequestAborted);
        Flash(Text.T("System.CleanedUp", result.Sessions, result.Imports, result.Files));
        return LocalRedirect("/Admin/System");
    }

    private async Task LoadAsync()
    {
        Counts = await instance.SnapshotAsync();
        Database = instance.DatabaseName();
        FreeBytes = instance.FreeSpace();
        var s = await instance.SettingsAsync();
        PublicUrlFixed = s.PublicUrlFixed;
        if (PublicUrlFixed)
        {
            PublicUrl = s.PublicUrl ?? string.Empty;
        }

        var storages = await db.Storages.AsNoTracking().Where(x => x.IsActive && !x.IsReadOnly).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Kind, x.IsLibrary }).ToListAsync();
        CacheChoices = [new(string.Empty, Text.T("System.Cache.DataVolume"), Text.T("System.Cache.DataVolume.Hint")),
            .. storages.Select(x => new Choice(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name, StorageAdmin.KindName(x.Kind)))];
        ImportChoices = [new(string.Empty, Text.T("System.ImportStorage.Ask")),
            .. storages.Where(x => x.IsLibrary).Select(x => new Choice(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name, StorageAdmin.KindName(x.Kind)))];
    }
}

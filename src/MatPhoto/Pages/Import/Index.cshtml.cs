using System.Globalization;
using MatPhoto.Controls;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Import;

/// <summary>
/// The start of an import: files of this device (chosen, a whole folder, or dropped; <c>import.js</c> sends them one by one and starts the analysis), or a folder of a
/// storage (the inbox the phone syncs to, a card reader), copied or moved. Either way the next page is the review of the suggested albums.
/// </summary>
public sealed class IndexModel(AppDbContext db, LibrarySettings settings) : AppPageModel
{
    [BindProperty] public string SourceStorageId { get; set; } = string.Empty;

    [BindProperty] public string SourcePath { get; set; } = string.Empty;

    [BindProperty] public bool MoveFiles { get; set; }

    public List<Choice> Sources { get; private set; } = [];

    /// <summary>The storages a source may be moved out of (the person is Editor there).</summary>
    public string MovableSources { get; private set; } = string.Empty;

    public string Accept => MediaFormats.AcceptList;

    public long MaxUploadBytes => settings.Current.MaxUploadBytes;

    public async Task<IActionResult> OnGetAsync()
    {
        if ((await AlbumService.TargetsAsync(db, UserId, HttpContext.RequestAborted)).Count == 0)
        {
            return NotFound();
        }

        await LoadAsync();
        SourceStorageId = Sources.FirstOrDefault()?.Value ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostStorageAsync()
    {
        var outcome = await ImportService.CreateFromStorageAsync(db, UserId, long.TryParse(SourceStorageId, out var id) ? id : 0, SourcePath, MoveFiles, HttpContext.RequestAborted);
        if (outcome.Ok)
        {
            HttpContext.RequestServices.GetRequiredService<ImportWorker>().Wake();
            return LocalRedirect($"/Import/Review/{outcome.Value:n}");
        }

        ModelState.AddModelError(nameof(SourcePath), outcome.Error!);
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var grants = await StorageAccess.ListForPersonAsync(db, UserId, HttpContext.RequestAborted);
        Sources = grants.Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Name, StorageAdmin.KindName(g.Kind))).ToList();
        MovableSources = string.Join(",", grants.Where(g => g.Role >= AccessRole.Editor && !g.IsReadOnly).Select(g => g.Id.ToString(CultureInfo.InvariantCulture)));
    }
}

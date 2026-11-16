using System.Globalization;
using MatPhoto.Controls;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Import;

/// <summary>
/// One import: while it is analysed or imported a progress (the page asks every two seconds), then the review – the suggested albums with their names (to change), whether
/// each is imported and into which album, the target storage and folder, the duplicates that stay out – and at the end what came of it.
/// </summary>
public sealed class ReviewModel(AppDbContext db, ImportWorker worker, LibrarySettings settings, IConfiguration configuration) : AppPageModel
{
    [BindProperty] public string TargetStorageId { get; set; } = string.Empty;

    [BindProperty] public string TargetFolder { get; set; } = string.Empty;

    [BindProperty] public List<GroupInput> Groups { get; set; } = [];

    public ImportView Import { get; private set; } = null!;

    public List<Choice> Targets { get; private set; } = [];

    public IReadOnlyList<AlbumChoice> Albums { get; private set; } = [];

    public string DateFormat => settings.Current.AlbumDateFormat;

    public sealed class GroupInput
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Include { get; set; }

        public string Album { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(Guid token)
    {
        if (!await LoadAsync(token))
        {
            return NotFound();
        }

        TargetStorageId = Import.TargetStorageId?.ToString(CultureInfo.InvariantCulture) ?? Targets.FirstOrDefault()?.Value ?? string.Empty;
        TargetFolder = Import.TargetFolder;
        return Page();
    }

    public Task<IActionResult> OnPostAsync(Guid token) => DecideAsync(token, start: true);

    public Task<IActionResult> OnPostSaveAsync(Guid token) => DecideAsync(token, start: false);

    public async Task<IActionResult> OnPostCancelAsync(Guid token)
    {
        if (!await ImportService.CancelAsync(db, UserId, token, configuration["DataDirectory"] ?? "/data", HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Import.Cancelled"));
        return LocalRedirect("/Import/History");
    }

    private async Task<IActionResult> DecideAsync(Guid token, bool start)
    {
        var decision = new ImportDecision(long.TryParse(TargetStorageId, out var target) ? target : null, TargetFolder,
            Groups.Select(g => new ImportGroupDecision(g.Id, g.Name, g.Include, long.TryParse(g.Album, out var album) ? album : null)).ToList());
        var (found, errors) = await ImportService.DecideAsync(db, UserId, token, decision, start, HttpContext.RequestAborted);
        if (!found)
        {
            return NotFound();
        }

        if (errors.Count > 0)
        {
            errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
            await LoadAsync(token);
            return Page();
        }

        if (start)
        {
            worker.Wake();
        }
        else
        {
            Flash(Text.T("Common.Saved"));
        }

        return LocalRedirect($"/Import/Review/{token:n}");
    }

    private async Task<bool> LoadAsync(Guid token)
    {
        var ct = HttpContext.RequestAborted;
        if (await ImportService.GetAsync(db, UserId, token, ct) is not { } view)
        {
            return false;
        }

        Import = view;
        Targets = (await AlbumService.TargetsAsync(db, UserId, ct)).Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Name, StorageAdmin.KindName(g.Kind))).ToList();
        if (view.State == ImportState.Review)
        {
            Albums = await ImportService.AlbumChoicesAsync(db, UserId, view, ct);
        }

        return true;
    }

    /// <summary>The choices of the album of a group: a new folder, or an album that exists (those of dates near the import).</summary>
    public List<Choice> AlbumOptions()
    {
        var list = new List<Choice> { new(string.Empty, Text.T("Import.NewAlbum")) };
        list.AddRange(Albums.Select(a => new Choice(a.Id.ToString(CultureInfo.InvariantCulture), a.Name)));
        return list;
    }
}

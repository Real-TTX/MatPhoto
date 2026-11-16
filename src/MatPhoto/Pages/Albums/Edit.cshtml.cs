using System.Globalization;
using MatPhoto.Controls;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Albums;

/// <summary>
/// Create or change an album. The album is a folder: its name is the date and the title (<c>2023.11.09 Hochzeit Julius</c>), a new album makes the folder in the
/// chosen storage and folder, a new name renames the folder. Delete moves the folder into the trash folder of the storage.
/// </summary>
public sealed class EditModel(AppDbContext db, StorageConnections connections, LibrarySettings settings) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string StorageId { get; set; } = string.Empty;

    [BindProperty] public string ParentFolder { get; set; } = string.Empty;

    [BindProperty] public string Date { get; set; } = string.Empty;

    [BindProperty] public string Title { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public string Heading { get; private set; } = string.Empty;

    public string FolderName { get; private set; } = string.Empty;

    public string Where { get; private set; } = string.Empty;

    public bool IsRoot { get; private set; }

    public List<Choice> Storages { get; private set; } = [];

    public string Example => AlbumNames.Compose(Dates.ParseIso(Date), Title, settings.Current.AlbumDateFormat);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await AlbumService.GetAsync(db, UserId, id, HttpContext.RequestAborted) is not { CanEdit: true } album)
            {
                return NotFound();
            }

            Date = album.AlbumDate is { } d ? Dates.Iso(d) : string.Empty;
            Title = album.Title;
            Description = album.Description ?? string.Empty;
            Show(album);
            return Page();
        }

        await LoadTargetsAsync();
        if (Storages.Count == 0)
        {
            return NotFound();
        }

        StorageId = Storages[0].Value;
        Date = Dates.Iso(Today);
        Heading = Text.T("Albums.New");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var input = new AlbumInput(long.TryParse(StorageId, out var s) ? s : 0, ParentFolder, Dates.ParseIso(Date), Title, Description, null);
        if (Id is { } id)
        {
            var current = await AlbumService.GetAsync(db, UserId, id, HttpContext.RequestAborted);
            var (found, errors) = await AlbumService.SaveAsync(db, connections, settings.Current, UserId, id, input with { CoverId = current?.CoverId }, HttpContext.RequestAborted);
            if (!found || current is null)
            {
                return NotFound();
            }

            if (errors.Count > 0)
            {
                errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
                Show(current);
                return Page();
            }

            Flash(Text.T("Common.SavedNamed", AlbumNames.Compose(input.Date, input.Title, settings.Current.AlbumDateFormat)));
            return LocalRedirect($"/Albums/View/{id}");
        }

        var (created, createErrors) = await AlbumService.CreateAsync(db, connections, settings.Current, UserId, input, HttpContext.RequestAborted);
        if (created is { } newId)
        {
            Flash(Text.T("Albums.Created", AlbumNames.Compose(input.Date, input.Title, settings.Current.AlbumDateFormat)));
            return LocalRedirect($"/Albums/View/{newId}");
        }

        createErrors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
        await LoadTargetsAsync();
        Heading = Text.T("Albums.New");
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await AlbumService.DeleteAsync(db, connections, UserId, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect($"/Albums/Edit?id={Id}");
        }

        Flash(Text.T("Albums.Trashed", outcome.Value!));
        return LocalRedirect("/Albums");
    }

    private void Show(AlbumDetail album)
    {
        Heading = album.Title.Length > 0 ? album.Title : album.Name;
        FolderName = album.Name;
        Where = album.StorageName + " / " + album.Path;
        IsRoot = album.Path.Length == 0;
    }

    private async Task LoadTargetsAsync()
        => Storages = (await AlbumService.TargetsAsync(db, UserId, HttpContext.RequestAborted))
            .Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Name, Services.Storages.StorageAdmin.KindName(g.Kind))).ToList();
}

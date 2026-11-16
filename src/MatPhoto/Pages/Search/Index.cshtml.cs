using MatPhoto.Data;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Search;

/// <summary>The search of the whole library: albums and lists by their names, the photos by file, album, place, camera, year and type (every word must match).</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "q")] public string? Q { get; set; }

    public string Query => (Q ?? string.Empty).Trim();

    public IReadOnlyList<AlbumCard> Albums { get; private set; } = [];

    public IReadOnlyList<CollectionRow> Lists { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (Query.Length == 0)
        {
            return;
        }

        var ct = HttpContext.RequestAborted;
        Albums = (await AlbumService.ListAsync(db, UserId, new AlbumCriteria(Query, null, null, "date", true, 1, 12), ct)).Items;
        Lists = (await CollectionService.ListAsync(db, UserId, new CollectionCriteria(Query, null, "name", false, 1, 6), ct)).Items;
    }
}

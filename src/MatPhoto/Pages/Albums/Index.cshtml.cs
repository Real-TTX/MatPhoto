using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using System.Globalization;

namespace MatPhoto.Pages.Albums;

/// <summary>The albums (the folders of the library storages) as cards: a toolbar (search, filter by storage and year, sort), the pager under them, "New album" below.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 48;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<AlbumCard> Items { get; private set; } = [];

    public bool CanCreate { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var storage = ListQuery.Text(Request, "storage");
        var year = ListQuery.Text(Request, "year");
        var sort = ListQuery.Text(Request, "sort") ?? "date";
        var desc = ListQuery.Descending(Request, byDefault: sort == "date");
        var page = ListQuery.Page(Request);
        var list = await AlbumService.ListAsync(db, UserId, new AlbumCriteria(search, long.TryParse(storage, out var s) ? s : null, int.TryParse(year, out var y) ? y : null, sort, desc, page, PageSize), ct);
        Items = list.Items;
        CanCreate = (await AlbumService.TargetsAsync(db, UserId, ct)).Count > 0;
        var filters = new List<FilterField>
        {
            new("year", Text.T("Albums.Year"), list.Years.Select(x => new FilterOption(x.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture))).ToList(), year),
        };
        if (list.Storages.Count > 1)
        {
            filters.Add(new FilterField("storage", Text.T("Albums.Storage"), list.Storages.Select(x => new FilterOption(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList(), storage));
        }

        Toolbar = new ToolbarModel
        {
            Id = "albums",
            Action = "/Albums",
            Label = Text.T("Albums.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Albums.Search"),
            Filters = filters,
            SortOptions = [new("date", Text.T("Albums.Sort.Date")), new("name", Text.T("Common.Name")), new("count", Text.T("Albums.Sort.Count"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "date",
            DefaultDescending = true,
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Albums.Many"),
            NounOne = Text.T("Albums.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}

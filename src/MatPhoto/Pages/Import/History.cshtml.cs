using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;

namespace MatPhoto.Pages.Import;

/// <summary>The imports of the person, newest first: a toolbar (filter by state), the table (when, from where, how many), the pager, "New import" below.</summary>
public sealed class HistoryModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ImportRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public bool CanImport { get; private set; }

    public async Task OnGetAsync()
    {
        var state = ListQuery.Text(Request, "state");
        var desc = ListQuery.Descending(Request, byDefault: true);
        var page = ListQuery.Page(Request);
        var all = await ImportService.HistoryAsync(db, UserId, 1000, HttpContext.RequestAborted);
        IEnumerable<ImportRow> query = all;
        if (Enum.TryParse<ImportState>(state, ignoreCase: true, out var s) && Enum.IsDefined(s))
        {
            query = query.Where(r => r.State == s);
        }

        query = desc ? query.OrderByDescending(r => r.CreateDate) : query.OrderBy(r => r.CreateDate);
        var matching = query.ToList();
        Items = ListQuery.Slice(matching, page, PageSize).ToList();
        CanImport = (await AlbumService.TargetsAsync(db, UserId, HttpContext.RequestAborted)).Count > 0;
        Toolbar = new ToolbarModel
        {
            Id = "imports",
            Action = "/Import/History",
            Label = Text.T("Import.History"),
            Filters = [new FilterField("state", Text.T("Common.Status"), Enum.GetValues<ImportState>().Select(x => new FilterOption(x.ToString().ToLowerInvariant(), Text.T("Import.State." + x))).ToList(), state)],
            SortOptions = [new("date", Text.T("Import.Col.Date"))],
            Sort = "date",
            Descending = desc,
            DefaultSort = "date",
            DefaultDescending = true,
            Total = all.Count,
            Matching = matching.Count,
            Noun = Text.T("Import.Many"),
            NounOne = Text.T("Import.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = Toolbar.UrlWithPage };
    }
}

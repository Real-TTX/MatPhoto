using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;

namespace MatPhoto.Pages.Lists;

/// <summary>The lists (linked albums) of the person and the shared ones: a toolbar (search, mine/shared, sort), the table, the pager, "New list" below.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<CollectionRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var show = ListQuery.Text(Request, "show");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await CollectionService.ListAsync(db, UserId, new CollectionCriteria(search, show, sort, desc, page, PageSize), HttpContext.RequestAborted);
        Items = list.Items;
        Toolbar = new ToolbarModel
        {
            Id = "lists",
            Action = "/Lists",
            Label = Text.T("Lists.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Lists.SearchLists"),
            Filters = [new FilterField("show", Text.T("Lists.Show"), [new("mine", Text.T("Lists.Mine")), new("shared", Text.T("Lists.Shared"))], show)],
            SortOptions = [new("name", Text.T("Common.Name")), new("count", Text.T("Albums.Sort.Count")), new("updated", Text.T("Lists.Updated"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Lists.Many"),
            NounOne = Text.T("Lists.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}

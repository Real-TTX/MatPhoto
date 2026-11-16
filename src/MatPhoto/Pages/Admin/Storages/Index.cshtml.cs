using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Admin.Storages;

/// <summary>
/// The storage locations of the installation (administrators): a toolbar (search, filter by type and state, sort), the table with type, place, reachability and
/// free space, and below it "New location" and "Check all now". An administrator manages a location here; browsing it needs a place on its access list too.
/// </summary>
public sealed class IndexModel(AppDbContext db, StorageConnections connections, StorageMonitor monitor) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public List<StorageRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public string StorageRoot => connections.StorageRoot;

    public async Task<IActionResult> OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Text(Request, "kind");
        var state = ListQuery.Text(Request, "state");
        var sort = ListQuery.Text(Request, "sort") is { } s && s is "name" or "kind" or "free" ? s : "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await StorageAdmin.ListAsync(db, connections, UserId, new StorageCriteria(search, kind, state, sort, desc, page, PageSize), HttpContext.RequestAborted);
        if (list is null)
        {
            return NotFound();
        }

        Items = list.Items;
        Toolbar = new ToolbarModel
        {
            Id = "storages",
            Action = "/Admin/Storages",
            Label = Text.T("Storages.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Storages.Admin.Search"),
            Filters =
            [
                new FilterField("kind", Text.T("Storages.Col.Kind"), Enum.GetValues<StorageKind>().Select(k => new FilterOption(k.ToString().ToLowerInvariant(), StorageAdmin.KindName(k))).ToList(), kind),
                new FilterField("state", Text.T("Common.Status"),
                [
                    new("reachable", Text.T("Storages.Reachable")), new("unreachable", Text.T("Storages.Unreachable")),
                    new("active", Text.T("Common.Active")), new("inactive", Text.T("Storages.Inactive")),
                ], state),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("kind", Text.T("Storages.Col.Kind")), new("photos", Text.T("Storages.Col.Photos")), new("free", Text.T("Storages.Col.Free"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Storages.Many"),
            NounOne = Text.T("Storages.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
        return Page();
    }

    /// <summary>"Check all now": every active location is asked at once (the list shows the outcome).</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var summary = await StorageAdmin.CheckAllAsync(db, monitor, UserId, HttpContext.RequestAborted);
        if (summary is null)
        {
            return NotFound();
        }

        if (summary.Unreachable == 0)
        {
            Flash(Text.T("Storages.Checked", summary.Reachable, summary.Total));
        }
        else
        {
            FlashError(Text.T("Storages.CheckedSome", summary.Unreachable, summary.Total));
        }

        return LocalRedirect("/Admin/Storages");
    }
}

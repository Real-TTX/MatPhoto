using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Shares;

/// <summary>
/// The links of the person (an administrator may show everybody's): a toolbar (search, sort), the table – what is shared, until when, downloads, visits – and the
/// pager. A link is made on the page of an album or a list ("Share"), so there is no "New" here.
/// </summary>
public sealed class IndexModel(AppDbContext db, LibrarySettings settings) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ShareRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    private string _origin = string.Empty;

    /// <summary>The whole address of a link (to copy).</summary>
    public string Address(string token) => $"{_origin}/s/{token}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!settings.Current.SharingEnabled)
        {
            return NotFound();
        }

        var search = ListQuery.Text(Request, "q");
        var show = IsAdmin ? ListQuery.Text(Request, "show") : null;
        var sort = ListQuery.Text(Request, "sort") ?? "created";
        var desc = ListQuery.Descending(Request, sort == "created");
        var page = ListQuery.Page(Request);
        var list = await ShareService.ListAsync(db, UserId, IsAdmin, new ShareCriteria(search, show, sort, desc, page, PageSize), HttpContext.RequestAborted);
        Items = list.Items;
        _origin = await OriginAsync();
        Toolbar = new ToolbarModel
        {
            Id = "shares",
            Action = "/Shares",
            Label = Text.T("Shares.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Shares.Search"),
            Filters = IsAdmin ? [new FilterField("show", Text.T("Shares.Show"), [new("all", Text.T("Shares.Everybody"))], show)] : [],
            SortOptions = [new("created", Text.T("Shares.Created")), new("expires", Text.T("Shares.Expires")), new("views", Text.T("Shares.Views"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "created",
            DefaultDescending = true,
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Shares.Many"),
            NounOne = Text.T("Shares.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
        return Page();
    }
}

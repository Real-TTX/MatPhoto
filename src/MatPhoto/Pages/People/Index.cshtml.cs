using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services;
using MatPhoto.Services.Faces;
using MatPhoto.Services.Library;

namespace MatPhoto.Pages.People;

/// <summary>
/// The people found on the photos as round cards: the named ones first, then the ones still without a name ("who is this?") by how many photos they are on. A toolbar
/// (search the names, filter named / unnamed / hidden, sort), the pager under them. While the recognition is off the page says so (and where it is switched on).
/// </summary>
public sealed class IndexModel(AppDbContext db, LibrarySettings settings, FaceEngine engine) : AppPageModel
{
    private const int PageSize = 60;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<PersonCard> Items { get; private set; } = [];

    public bool Enabled { get; private set; }

    /// <summary>How far the recognition is (photos looked at of all), while it is not through.</summary>
    public (int Looked, int Photos)? Progress { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Enabled = settings.Current.FacesEnabled && engine.IsAvailable;
        var search = ListQuery.Text(Request, "q");
        var filter = ListQuery.Text(Request, "show");
        var sort = ListQuery.Text(Request, "sort") ?? "count";
        var desc = ListQuery.Descending(Request, byDefault: sort == "count");
        var page = ListQuery.Page(Request);
        var list = Enabled ? await PeopleService.ListAsync(db, UserId, new PeopleCriteria(search, filter, sort, desc, page, PageSize), ct) : new PeopleList([], 0, 0);
        Items = list.Items;
        if (Enabled)
        {
            var stats = await PeopleService.StatsAsync(db, true, true, ct);
            Progress = stats.Looked + stats.Failed < stats.Photos ? (stats.Looked + stats.Failed, stats.Photos) : null;
        }

        Toolbar = new ToolbarModel
        {
            Id = "people",
            Action = "/People",
            Label = Text.T("People.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("People.Search"),
            Filters =
            [
                new FilterField("show", Text.T("People.Show"),
                    [new("named", Text.T("People.Show.named")), new("unnamed", Text.T("People.Show.unnamed")), new("hidden", Text.T("People.Show.hidden"))], filter),
            ],
            SortOptions = [new("count", Text.T("People.Sort.Count")), new("name", Text.T("Common.Name"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "count",
            DefaultDescending = true,
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("People.Many"),
            NounOne = Text.T("People.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}

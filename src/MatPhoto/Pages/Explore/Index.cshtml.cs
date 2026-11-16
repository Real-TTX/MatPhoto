using MatPhoto.Data;
using MatPhoto.Services.Library;

namespace MatPhoto.Pages.Explore;

/// <summary>Explore: the automatic albums – places (towns and countries, read offline from the positions), types (videos, screenshots, selfies …), years, cameras.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ExploreModel Explore { get; private set; } = new([], [], [], [], []);

    public async Task OnGetAsync() => Explore = await LibraryService.ExploreAsync(db, UserId, HttpContext.RequestAborted);
}

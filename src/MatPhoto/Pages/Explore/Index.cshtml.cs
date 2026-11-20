using MatPhoto.Data;
using MatPhoto.Services.Faces;
using MatPhoto.Services.Library;

namespace MatPhoto.Pages.Explore;

/// <summary>
/// Explore: the automatic albums – people (when the recognition is on), places (towns and countries, read offline from the positions), types (videos, screenshots,
/// selfies …), years, cameras.
/// </summary>
public sealed class IndexModel(AppDbContext db, LibrarySettings settings, FaceEngine engine) : AppPageModel
{
    public ExploreModel Explore { get; private set; } = new([], [], [], [], []);

    public IReadOnlyList<PersonCard> People { get; private set; } = [];

    public bool PeopleOn { get; private set; }

    public async Task OnGetAsync()
    {
        Explore = await LibraryService.ExploreAsync(db, UserId, HttpContext.RequestAborted);
        PeopleOn = settings.Current.FacesEnabled && engine.IsAvailable;
        People = PeopleOn ? await PeopleService.TopAsync(db, UserId, 12, HttpContext.RequestAborted) : [];
    }
}

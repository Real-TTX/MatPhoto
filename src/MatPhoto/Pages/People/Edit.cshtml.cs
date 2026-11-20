using System.Globalization;
using MatPhoto.Controls;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Faces;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.People;

/// <summary>
/// Change a person: the name (a name another person has joins the two), hidden among the people or not, joined into another person, the face that shows them, and the
/// faces that are not them ("not this person": they never come back). Delete forgets the person: their faces stay without a person and are not grouped again.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    /// <summary>The faces shown to choose from (the newest).</summary>
    private const int FacesShown = 240;

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public bool Hidden { get; set; }

    [BindProperty] public string MergeInto { get; set; } = string.Empty;

    [BindProperty] public string Cover { get; set; } = string.Empty;

    [BindProperty] public List<long> NotThem { get; set; } = [];

    public PersonDetail Person { get; private set; } = null!;

    public IReadOnlyList<PersonFace> Faces { get; private set; } = [];

    public List<Choice> MergeChoices { get; private set; } = [];

    public string Heading => Person.Name ?? Text.T("People.WhoIsThis");

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Name = Person.Name ?? string.Empty;
        Hidden = Person.Hidden;
        Cover = Person.CoverFaceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var removed = await PeopleService.RejectAsync(db, UserId, Id, NotThem, ct);
        if (long.TryParse(Cover, NumberStyles.None, CultureInfo.InvariantCulture, out var cover) && !NotThem.Contains(cover))
        {
            await PeopleService.SetCoverAsync(db, UserId, Id, cover, ct);
        }

        var outcome = await PeopleService.SaveAsync(db, UserId, Id,
            new PersonInput(Name, Hidden, long.TryParse(MergeInto, NumberStyles.None, CultureInfo.InvariantCulture, out var into) ? into : null), ct);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Flash(removed > 0 ? Text.T("People.SavedRemoved", removed) : Text.T("Common.Saved"));
        return LocalRedirect($"/People/View/{outcome.Value}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync() || !await PeopleService.ForgetAsync(db, UserId, Id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("People.Forgot"));
        return LocalRedirect("/People");
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (await PeopleService.GetAsync(db, UserId, Id, ct) is not { CanEdit: true } person)
        {
            return false;
        }

        Person = person;
        Faces = await PeopleService.FacesAsync(db, UserId, Id, FacesShown, ct);
        var others = await PeopleService.ListAsync(db, UserId, new PeopleCriteria(null, null, "name", false, 1, 500), ct);
        MergeChoices = [new(string.Empty, Text.T("People.MergeNone")),
            .. others.Items.Where(o => o.Id != Id).Select(o => new Choice(o.Id.ToString(CultureInfo.InvariantCulture),
                o.Name ?? Text.T("People.UnnamedNumber", o.Id), Text.T("People.Photos", o.Photos)))];
        return true;
    }
}

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

    /// <summary>Where the ticked faces go: a person's name (a new one makes a person); empty: they only leave this person.</summary>
    [BindProperty] public string MoveTo { get; set; } = string.Empty;

    public PersonDetail Person { get; private set; } = null!;

    /// <summary>The names of the other people (the suggestions of <see cref="MoveTo"/>).</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

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

        // the ticked faces go to the person named, or only leave this one
        string? message = null;
        if (NotThem.Count > 0 && PeopleService.Clean(MoveTo) is { } other)
        {
            var mine = Faces.Select(f => f.Id).ToHashSet();
            var moved = await PeopleService.AssignAsync(db, UserId, NotThem.Where(mine.Contains).ToList(), other, ct);
            if (!moved.Ok)
            {
                return NotFound();
            }

            if (await PeopleService.GetAsync(db, UserId, Id, ct) is null)
            {
                // every face went: this person is the other one now
                Flash(Text.T("People.Moved", NotThem.Count, other));
                return LocalRedirect($"/People/View/{moved.Value}");
            }

            message = Text.T("People.Moved", NotThem.Count, other);
            NotThem = [];
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

        Flash(message ?? (removed > 0 ? Text.T("People.SavedRemoved", removed) : Text.T("Common.Saved")));
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
        Names = (await PeopleService.NamesAsync(db, UserId, ct)).Where(n => n.Id != Id).Select(n => n.Name).ToList();
        var others = await PeopleService.ListAsync(db, UserId, new PeopleCriteria(null, null, "name", false, 1, 500), ct);
        MergeChoices = [new(string.Empty, Text.T("People.MergeNone")),
            .. others.Items.Where(o => o.Id != Id).Select(o => new Choice(o.Id.ToString(CultureInfo.InvariantCulture),
                o.Name ?? Text.T("People.UnnamedNumber", o.Id), Text.T("People.Photos", o.Photos)))];
        return true;
    }
}

using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Faces;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.People;

/// <summary>
/// One person: their photos as a grid (newest first). A person without a name asks "who is this?" above the grid: a name another person already has joins the two.
/// </summary>
public sealed class ViewModel(AppDbContext db) : AppPageModel
{
    public PersonDetail Person { get; private set; } = null!;

    /// <summary>The names already given (the suggestions of the name field).</summary>
    public IReadOnlyList<(long Id, string Name)> Names { get; private set; } = [];

    [BindProperty] public string Name { get; set; } = string.Empty;

    public string Title => Person.Name ?? Text.T("People.WhoIsThis");

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (await PeopleService.GetAsync(db, UserId, id, HttpContext.RequestAborted) is not { } person)
        {
            return NotFound();
        }

        Person = person;
        Names = person.CanEdit && person.Name is null ? await PeopleService.NamesAsync(db, UserId, HttpContext.RequestAborted) : [];
        return Page();
    }

    /// <summary>The name of the form "who is this?".</summary>
    public async Task<IActionResult> OnPostNameAsync(long id)
    {
        var outcome = await PeopleService.RenameAsync(db, UserId, id, Name, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Flash(Text.T("People.Named", PeopleService.Clean(Name) ?? string.Empty));
        return LocalRedirect($"/People/View/{outcome.Value}");
    }
}

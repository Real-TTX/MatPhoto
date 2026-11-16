using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Lists;

/// <summary>Create or change a list of the person: name, description, colour, shared with everybody. Delete removes the list (the photos stay in their albums).</summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Colour { get; set; } = string.Empty;

    [BindProperty] public bool IsShared { get; set; }

    public bool IsNew => Id is null;

    public string Heading { get; private set; } = string.Empty;

    private long? _cover;

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await CollectionService.GetAsync(db, UserId, id, HttpContext.RequestAborted) is not { IsMine: true } list)
            {
                return NotFound();
            }

            Name = list.Name;
            Description = list.Description ?? string.Empty;
            Colour = list.Colour ?? string.Empty;
            IsShared = list.IsShared;
            Heading = list.Name;
        }
        else
        {
            Heading = Text.T("Lists.New");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Id is { } existing && await CollectionService.GetAsync(db, UserId, existing, HttpContext.RequestAborted) is { IsMine: true } current)
        {
            _cover = current.CoverId;
        }

        var (saved, errors) = await CollectionService.SaveAsync(db, UserId, Id, new CollectionInput(Name, Description, Colour, IsShared, _cover), HttpContext.RequestAborted);
        if (saved is { } id)
        {
            Flash(Text.T(Id is null ? "Common.CreatedNamed" : "Common.SavedNamed", Name.Trim()));
            return LocalRedirect($"/Lists/View/{id}");
        }

        if (errors.Count == 0)
        {
            return NotFound();
        }

        errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
        Heading = Id is null ? Text.T("Lists.New") : Name;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (await CollectionService.DeleteAsync(db, UserId, Id ?? 0, HttpContext.RequestAborted) is not { } name)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", name));
        return LocalRedirect("/Lists");
    }
}

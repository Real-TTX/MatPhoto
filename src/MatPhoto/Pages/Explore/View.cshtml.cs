using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Explore;

/// <summary>One automatic album of Explore: a place, a type, a year or a camera (<c>?scope=place:DE/Berlin</c>) as a grid.</summary>
public sealed class ViewModel : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "scope")] public string? ScopeText { get; set; }

    public string Scope { get; private set; } = "all";

    public string Title { get; private set; } = string.Empty;

    public IActionResult OnGet()
    {
        var scope = MediaScope.Parse(ScopeText);
        if (scope.IsAlbum || scope.IsList)
        {
            return LocalRedirect(scope.IsAlbum ? $"/Albums/View/{scope.Id}" : $"/Lists/View/{scope.Id}");
        }

        Scope = scope.ToString();
        Title = LibraryService.TitleOf(scope);
        return Page();
    }
}

using MatPhoto.Data;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Lists;

/// <summary>One list: its photos in the order they were put on it (only those of storages the person may see).</summary>
public sealed class ViewModel(AppDbContext db) : AppPageModel
{
    public CollectionDetail List { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (await CollectionService.GetAsync(db, UserId, id, HttpContext.RequestAborted) is not { } list)
        {
            return NotFound();
        }

        List = list;
        return Page();
    }
}

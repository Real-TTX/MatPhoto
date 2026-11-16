using MatPhoto.Data;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Albums;

/// <summary>One album: its folder, its description and its photos from the first to the last.</summary>
public sealed class ViewModel(AppDbContext db) : AppPageModel
{
    public AlbumDetail Album { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (await AlbumService.GetAsync(db, UserId, id, HttpContext.RequestAborted) is not { } album)
        {
            return NotFound();
        }

        Album = album;
        return Page();
    }
}

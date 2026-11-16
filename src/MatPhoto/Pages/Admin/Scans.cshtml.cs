using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Pages.Admin;

/// <summary>The scans of the library storages for administrators: how far each is (the page asks again while one runs), the last result, "Scan now".</summary>
public sealed class ScansModel(AppDbContext db, LibraryScanner scanner, VideoTools video) : AppPageModel
{
    public sealed record Row(long Id, string Name, int Interval, DateTime? LastScan, int Media, ScanProgress? Progress);

    public IReadOnlyList<Row> Items { get; private set; } = [];

    public bool Video => video.IsAvailable;

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var rows = await db.Storages.AsNoTracking().Where(s => s.IsActive && s.IsLibrary).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.ScanIntervalMinutes, s.LastScanDate, Media = db.Media.Count(m => m.StorageId == s.Id) })
            .ToListAsync(HttpContext.RequestAborted);
        Items = rows.Select(r => new Row(r.Id, r.Name, r.ScanIntervalMinutes, r.LastScanDate, r.Media, scanner.ProgressOf(r.Id))).ToList();
    }

    public async Task<IActionResult> OnPostScanAsync(long id)
    {
        if (!await db.Storages.AnyAsync(s => s.Id == id && s.IsActive && s.IsLibrary))
        {
            return NotFound();
        }

        scanner.Request(id);
        Flash(Text.T("Scan.Started"));
        return LocalRedirect("/Admin/Scans");
    }

    public async Task<IActionResult> OnPostScanAllAsync()
    {
        var ids = await db.Storages.Where(s => s.IsActive && s.IsLibrary).Select(s => s.Id).ToListAsync();
        ids.ForEach(scanner.Request);
        Flash(Text.T("Scan.StartedAll", ids.Count));
        return LocalRedirect("/Admin/Scans");
    }
}

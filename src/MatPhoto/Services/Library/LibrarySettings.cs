using MatPhoto.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>The settings the background work needs, as they were saved last (a copy, read without a query).</summary>
public sealed record LibraryOptions(
    long? CacheStorageId,
    string CacheFolder,
    int ThumbnailHeight,
    int PreviewSize,
    int ThumbnailQuality,
    string AlbumDateFormat,
    int ImportGapHours,
    int ImportDistanceKm,
    int SimilarThreshold,
    long MaxUploadBytes,
    bool FacesEnabled = false)
{
    public static LibraryOptions From(SystemSetting s) => new(
        s.CacheStorageId, string.IsNullOrWhiteSpace(s.CacheFolder) ? ".matphoto-cache" : s.CacheFolder, Math.Clamp(s.ThumbnailHeight, 160, 1024), Math.Clamp(s.PreviewSize, 800, 4096),
        Math.Clamp(s.ThumbnailQuality, 50, 95), string.IsNullOrWhiteSpace(s.AlbumDateFormat) ? "yyyy.MM.dd" : s.AlbumDateFormat, Math.Clamp(s.ImportGapHours, 1, 240),
        Math.Clamp(s.ImportDistanceKm, 1, 5000), Math.Clamp(s.SimilarThreshold, 0, 12), Math.Max(0, s.MaxUploadBytes), s.FacesEnabled);
}

/// <summary>
/// The settings of the library in memory (one per app): read at the start and again whenever the administration saves them (<see cref="Reload"/>), so a scan of
/// thousands of photos does not ask the database for them each time.
/// </summary>
public sealed class LibrarySettings(IServiceScopeFactory scopes)
{
    private LibraryOptions? _current;

    public LibraryOptions Current => _current ?? Load();

    public LibraryOptions Reload(SystemSetting s) => _current = LibraryOptions.From(s);

    private LibraryOptions Load()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.Id == 1) ?? new SystemSetting();
        return _current = LibraryOptions.From(s);
    }
}

using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services;

/// <summary>The numbers of the installation (the platform tiles, the admin page System). Nothing here says what is on a photo: only how many and how large.</summary>
public sealed record InstanceSnapshot(
    int Users,
    int Admins,
    int Storages,
    int Albums,
    int Photos,
    int Videos,
    long LibraryBytes,
    int Pending,
    int Failed,
    int Lists,
    int Imports7Days,
    long CacheBytes,
    DateTime? LastScanUtc,
    DateTime? LastActivityUtc);

/// <summary>
/// The settings of the system (admin page System and the platform contract). <see cref="PublicUrl"/> is the address in force: the value of <c>MAT_PUBLIC_URL</c>
/// while it is set (then <see cref="PublicUrlFixed"/>), else the stored one.
/// </summary>
public sealed record SystemSettings(
    bool RegistrationOpen,
    string? PublicUrl,
    bool PublicUrlFixed,
    long? CacheStorageId,
    string CacheFolder,
    int ThumbnailHeight,
    int PreviewSize,
    int ThumbnailQuality,
    string AlbumDateFormat,
    int ImportGapHours,
    int ImportDistanceKm,
    long? ImportStorageId,
    string ImportFolder,
    int SimilarThreshold,
    long MaxUploadBytes,
    bool FacesEnabled);

/// <summary>What the housekeeping removed.</summary>
public sealed record CleanUpResult(int Sessions, int Imports, int Files);

/// <summary>
/// The installation as a whole: its numbers, the settings of the system (registration, public address, the cache of the thumbnails, the import), the state of the
/// database and the housekeeping. The admin page System, the platform contract (<c>Platform/MatPhotoPlatform</c>) and the background housekeeping go through it.
/// A broker such as Mattiverse may set the public address (<c>MAT_PUBLIC_URL</c>): then it wins and the stored value stays as it is.
/// </summary>
public sealed class InstanceService(AppDbContext db, AccountService accounts, MatPlatformSettings platform, LibrarySettings library, ThumbnailCache cache, IConfiguration configuration)
{
    private readonly string _data = configuration["DataDirectory"] ?? "/data";

    /// <summary>The public address in force: <c>MAT_PUBLIC_URL</c>, else the stored one; null = the address of the request.</summary>
    public static string? PublicUrlOf(SystemSetting s, MatPlatformSettings platform) => platform.AppPublicUrl ?? NormalizePublicUrl(s.PublicUrl);

    /// <summary>Can the database be reached (the health check)?</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Can the data folder be written (the keys, the cache, the uploads of an import)?</summary>
    public bool CanWriteFiles()
    {
        try
        {
            Directory.CreateDirectory(_data);
            var probe = Path.Combine(_data, $".probe-{Guid.NewGuid():n}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The free space of the data volume in bytes (-1 when unknown).</summary>
    public long FreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(_data)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public async Task<InstanceSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var shown = db.Media.Where(m => (m.Traits & MediaTraits.LiveVideo) == 0);
        return new InstanceSnapshot(
            Users: await db.Users.CountAsync(u => u.IsActive, ct),
            Admins: await db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct),
            Storages: await db.Storages.CountAsync(s => s.IsActive, ct),
            Albums: await db.Albums.CountAsync(a => a.MediaCount > 0, ct),
            Photos: await shown.CountAsync(m => m.Type == MediaType.Photo, ct),
            Videos: await shown.CountAsync(m => m.Type == MediaType.Video, ct),
            LibraryBytes: await db.Media.SumAsync(m => (long?)m.Size, ct) ?? 0,
            Pending: await db.Media.CountAsync(m => m.ThumbState == ThumbState.Pending, ct),
            Failed: await db.Media.CountAsync(m => m.ThumbState == ThumbState.Failed, ct),
            Lists: await db.Collections.CountAsync(ct),
            Imports7Days: await db.ImportSessions.CountAsync(s => s.State == ImportState.Done && s.FinishedDate >= weekAgo, ct),
            CacheBytes: library.Current.CacheStorageId is null ? cache.LocalBytes() : -1,
            LastScanUtc: await db.Storages.MaxAsync(s => s.LastScanDate, ct),
            LastActivityUtc: await db.Activities.MaxAsync(a => (DateTime?)a.OccurredDate, ct));
    }

    public Task<bool> AnyUserAsync() => accounts.AnyUserAsync();

    /// <summary>Where the data is: <c>PostgreSQL host:port/database</c> (for the admin page; never in the platform API).</summary>
    public string DatabaseName()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        return $"PostgreSQL {cs.Host}:{cs.Port}/{cs.Database}";
    }

    public async Task<SystemSettings> SettingsAsync()
    {
        var s = await accounts.SettingsAsync();
        return new SystemSettings(s.RegistrationOpen, PublicUrlOf(s, platform), platform.AppPublicUrl is not null, s.CacheStorageId, s.CacheFolder, s.ThumbnailHeight, s.PreviewSize,
            s.ThumbnailQuality, s.AlbumDateFormat, s.ImportGapHours, s.ImportDistanceKm, s.ImportStorageId, s.ImportFolder, s.SimilarThreshold, s.MaxUploadBytes, s.FacesEnabled);
    }

    /// <summary>
    /// Saves the settings of the system; a <c>null</c> leaves a value as it is, an empty address clears it. An address that <c>MAT_PUBLIC_URL</c> sets is left as it is.
    /// Answers the field and the message of the first wrong value (nothing is saved then), else <c>null</c>. A new place of the cache makes every thumbnail again.
    /// </summary>
    public async Task<(string Field, string Message)?> SaveSettingsAsync(SettingsChange change)
    {
        var publicUrl = platform.AppPublicUrl is null ? change.PublicUrl : null;
        string? url = null;
        if (publicUrl is not null)
        {
            url = NormalizePublicUrl(publicUrl);
            if (url is not null && !IsPublicUrl(url))
            {
                return ("PublicUrl", Text.T("System.PublicUrl.Invalid"));
            }
        }

        if (change.ThumbnailHeight is < 160 or > 1024)
        {
            return ("ThumbnailHeight", Text.T("System.Range", 160, 1024));
        }

        if (change.PreviewSize is < 800 or > 4096)
        {
            return ("PreviewSize", Text.T("System.Range", 800, 4096));
        }

        if (change.ThumbnailQuality is < 50 or > 95)
        {
            return ("ThumbnailQuality", Text.T("System.Range", 50, 95));
        }

        if (change.ImportGapHours is < 1 or > 240)
        {
            return ("ImportGapHours", Text.T("System.Range", 1, 240));
        }

        if (change.ImportDistanceKm is < 1 or > 5000)
        {
            return ("ImportDistanceKm", Text.T("System.Range", 1, 5000));
        }

        if (change.SimilarThreshold is < 0 or > 12)
        {
            return ("SimilarThreshold", Text.T("System.Range", 0, 12));
        }

        if (change.MaxUploadBytes < 0)
        {
            return ("MaxUploadMb", Text.T("System.Number.Invalid"));
        }

        if (change.AlbumDateFormat is { } format && !IsDateFormat(format))
        {
            return ("AlbumDateFormat", Text.T("System.DateFormat.Invalid"));
        }

        var cacheFolder = change.CacheFolder?.Trim().Trim('/');
        if (cacheFolder is not null && (cacheFolder.Length == 0 || cacheFolder.Contains("..", StringComparison.Ordinal) || cacheFolder.Length > 500))
        {
            return ("CacheFolder", Text.T("System.Folder.Invalid"));
        }

        var importFolder = change.ImportFolder?.Trim().Trim('/');
        if (importFolder is not null && (importFolder.Contains("..", StringComparison.Ordinal) || importFolder.Length > 500))
        {
            return ("ImportFolder", Text.T("System.Folder.Invalid"));
        }

        if (change.CacheStorageId is { } cacheStorage && cacheStorage > 0 && !await db.Storages.AnyAsync(x => x.Id == cacheStorage && x.IsActive && !x.IsReadOnly))
        {
            return ("CacheStorageId", Text.T("System.Cache.Invalid"));
        }

        if (change.ImportStorageId is { } importStorage && importStorage > 0 && !await db.Storages.AnyAsync(x => x.Id == importStorage && x.IsActive && x.IsLibrary && !x.IsReadOnly))
        {
            return ("ImportStorageId", Text.T("System.ImportStorage.Invalid"));
        }

        var s = await accounts.SettingsAsync();
        var cacheBefore = (s.CacheStorageId, s.CacheFolder, s.ThumbnailHeight, s.PreviewSize, s.ThumbnailQuality);
        if (change.RegistrationOpen is { } open)
        {
            s.RegistrationOpen = open;
        }

        if (publicUrl is not null)
        {
            s.PublicUrl = url;
        }

        if (change.CacheStorageId is { } cs)
        {
            s.CacheStorageId = cs > 0 ? cs : null;
        }

        s.CacheFolder = cacheFolder ?? s.CacheFolder;
        s.ThumbnailHeight = change.ThumbnailHeight ?? s.ThumbnailHeight;
        s.PreviewSize = change.PreviewSize ?? s.PreviewSize;
        s.ThumbnailQuality = change.ThumbnailQuality ?? s.ThumbnailQuality;
        s.AlbumDateFormat = change.AlbumDateFormat ?? s.AlbumDateFormat;
        s.ImportGapHours = change.ImportGapHours ?? s.ImportGapHours;
        s.ImportDistanceKm = change.ImportDistanceKm ?? s.ImportDistanceKm;
        if (change.ImportStorageId is { } istorage)
        {
            s.ImportStorageId = istorage > 0 ? istorage : null;
        }

        s.ImportFolder = importFolder ?? s.ImportFolder;
        s.SimilarThreshold = change.SimilarThreshold ?? s.SimilarThreshold;
        s.MaxUploadBytes = change.MaxUploadBytes ?? s.MaxUploadBytes;
        s.FacesEnabled = change.FacesEnabled ?? s.FacesEnabled;
        await db.SaveChangesAsync();
        library.Reload(s);

        // the thumbnails live somewhere else now, or are made in another size: they are made again by the next scans
        if (cacheBefore != (s.CacheStorageId, s.CacheFolder, s.ThumbnailHeight, s.PreviewSize, s.ThumbnailQuality))
        {
            cache.Changed();
            await db.Media.Where(m => m.ThumbState != ThumbState.Pending).ExecuteUpdateAsync(u => u.SetProperty(m => m.ThumbState, ThumbState.Pending));
        }

        return null;
    }

    /// <summary>The address without spaces and the last slash; <c>null</c> when empty.</summary>
    public static string? NormalizePublicUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>A date format for folder names: only digits of the date and separators that every file system takes.</summary>
    public static bool IsDateFormat(string format)
        => format.Length is > 0 and <= 40 && format.All(c => c is 'y' or 'M' or 'd' or '.' or '-' or '_' or ' ') && format.Contains("yyyy", StringComparison.Ordinal);

    /// <summary>
    /// The housekeeping: ended sessions go; imports that were cancelled, failed or left waiting for a week go with their uploads; temporary files of the indexer older
    /// than a day go.
    /// </summary>
    public async Task<CleanUpResult> CleanUpAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sessions = await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        var weekAgo = now.AddDays(-7);
        var old = await db.ImportSessions
            .Where(s => (s.State == ImportState.Cancelled || s.State == ImportState.Failed || s.State == ImportState.Collecting || s.State == ImportState.Review) && s.UpdateDate < weekAgo)
            .ToListAsync(ct);
        foreach (var session in old)
        {
            ImportService.DeleteStaging(_data, session.Token);
            if (session.State is ImportState.Collecting or ImportState.Review)
            {
                session.State = ImportState.Cancelled;
                session.FinishedDate = now;
            }
        }

        await db.SaveChangesAsync(ct);
        var files = 0;
        var temp = Path.Combine(_data, "tmp");
        if (Directory.Exists(temp))
        {
            foreach (var file in new DirectoryInfo(temp).EnumerateFiles().Where(f => f.LastWriteTimeUtc < now.AddDays(-1)))
            {
                try
                {
                    file.Delete();
                    files++;
                }
                catch (IOException)
                {
                    // in use: next time
                }
            }
        }

        return new CleanUpResult(sessions, old.Count, files);
    }
}

/// <summary>A change of the settings of the system: <c>null</c> leaves a value as it is (a storage id of 0 clears the choice).</summary>
public sealed record SettingsChange(
    bool? RegistrationOpen = null,
    string? PublicUrl = null,
    long? CacheStorageId = null,
    string? CacheFolder = null,
    int? ThumbnailHeight = null,
    int? PreviewSize = null,
    int? ThumbnailQuality = null,
    string? AlbumDateFormat = null,
    int? ImportGapHours = null,
    int? ImportDistanceKm = null,
    long? ImportStorageId = null,
    string? ImportFolder = null,
    int? SimilarThreshold = null,
    long? MaxUploadBytes = null,
    bool? FacesEnabled = null);

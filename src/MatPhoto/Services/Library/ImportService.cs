using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

public sealed record ImportItemView(long Id, string FileName, string SourcePath, long Size, MediaType Type, MediaTraits Traits, DateTime? TakenDate, DateSource DateSource, string? City,
    string? Country, string? CameraModel, string Hash, bool HasThumb, long? DuplicateOfMediaId, long? DuplicateOfItemId, ImportItemState State, string? Error, string? TargetPath, int Width, int Height);

public sealed record ImportGroupView(long Id, int Position, string Name, DateTime? StartDate, DateTime? EndDate, string? Place, string Reason, bool Include, long? ExistingAlbumId,
    IReadOnlyList<ImportItemView> Items);

public sealed record ImportView(long Id, Guid Token, ImportSource Source, string? SourceName, string? SourcePath, bool MoveFiles, ImportState State, long? TargetStorageId, string TargetFolder,
    int TotalCount, int DoneCount, int ImportedCount, int SkippedCount, int FailedCount, string? Message, DateTime CreateDate, DateTime? FinishedDate, IReadOnlyList<ImportGroupView> Groups,
    IReadOnlyList<ImportItemView> Duplicates, IReadOnlyList<ImportItemView> Unreadable);

public sealed record ImportRow(Guid Token, ImportSource Source, string? SourceName, ImportState State, int TotalCount, int ImportedCount, int SkippedCount, int FailedCount, DateTime CreateDate,
    DateTime? FinishedDate);

/// <summary>What the person decided on the review page: the target and, per group, its name, whether it is imported and into which album.</summary>
public sealed record ImportDecision(long? TargetStorageId, string? TargetFolder, IReadOnlyList<ImportGroupDecision> Groups);

public sealed record ImportGroupDecision(long Id, string? Name, bool Include, long? ExistingAlbumId);

/// <summary>An album that exists and might be the right one for a group (the select on the review page).</summary>
public sealed record AlbumChoice(long Id, string Name, long StorageId, DateTime? StartDate);

/// <summary>
/// The smart import. The person brings files (uploads them, or names a folder of a storage: the inbox the phone syncs to, a card), MatPhoto reads every file (date,
/// place, camera, type, its thumbnail), recognises what is in the library already and what came twice, and suggests albums: photos taken close together in time and
/// place are one album, named in the pattern of the installation (<c>2023.11.09 Hamburg</c>, or the name of the folder they came from), screenshots and files without a
/// date apart. The person checks the names and MatPhoto copies (or moves) the files into the folders of the target storage, then scans them. The work runs in the
/// background (<see cref="ImportWorker"/>); these functions check that the import belongs to the person (else null = 404).
/// </summary>
public static class ImportService
{
    /// <summary>The folder of the uploads of an import in the data volume.</summary>
    public static string StagingFolder(string dataDirectory, Guid token) => Path.Combine(dataDirectory, "import", token.ToString("n"));

    public static async Task<Guid> CreateUploadAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var settings = await SettingsStore.LoadAsync(db, ct);
        var session = db.ImportSessions.Add(new ImportSession
        {
            UserId = userId, Source = ImportSource.Upload, State = ImportState.Collecting, TargetStorageId = settings.ImportStorageId, TargetFolder = settings.ImportFolder,
        }).Entity;
        await db.SaveChangesAsync(ct);
        return session.Token;
    }

    /// <summary>An import of a folder of a storage (moving the files needs the role Editor on it).</summary>
    public static async Task<Outcome<Guid>> CreateFromStorageAsync(AppDbContext db, long userId, long storageId, string? path, bool move, CancellationToken ct = default)
    {
        var found = await StorageAccess.FindAsync(db, userId, storageId, move ? AccessRole.Editor : AccessRole.Viewer, ct);
        if (found is null)
        {
            return Outcome<Guid>.Fail(Text.T(move ? "Import.Error.SourceMove" : "Import.Error.Source"));
        }

        string folder;
        try
        {
            folder = PathGuard.Normalize(path ?? string.Empty);
        }
        catch (StorageException)
        {
            return Outcome<Guid>.Fail(Text.T("Import.Error.Folder"));
        }

        var settings = await SettingsStore.LoadAsync(db, ct);
        var session = db.ImportSessions.Add(new ImportSession
        {
            UserId = userId, Source = ImportSource.Storage, SourceStorageId = storageId, SourcePath = folder, MoveFiles = move && !found.Value.Storage.IsReadOnly,
            State = ImportState.Analyzing, TargetStorageId = settings.ImportStorageId ?? (found.Value.Storage.IsLibrary && found.Value.Role >= AccessRole.Editor ? storageId : null),
            TargetFolder = settings.ImportFolder,
        }).Entity;
        await db.SaveChangesAsync(ct);
        return Outcome<Guid>.Success(session.Token);
    }

    /// <summary>Takes one uploaded file into the import (only while it collects files). Answers an error in words, or null.</summary>
    public static async Task<string?> AddUploadAsync(AppDbContext db, long userId, Guid token, string dataDirectory, string? fileName, string? relativePath, Stream content, long? length,
        LibraryOptions options, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(s => s.Token == token && s.UserId == userId && s.State == ImportState.Collecting, ct);
        if (session is null)
        {
            return Text.T("Import.Error.NotCollecting");
        }

        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        if (name.Length == 0 || !MediaFormats.IsMedia(name))
        {
            return Text.T("Import.Error.Type", name);
        }

        if (options.MaxUploadBytes > 0 && length > options.MaxUploadBytes)
        {
            return Text.T("Import.Error.TooLarge", name, Ui.Bytes(options.MaxUploadBytes));
        }

        var folder = StagingFolder(dataDirectory, token);
        Directory.CreateDirectory(folder);
        var stored = $"{Guid.NewGuid():n}{Path.GetExtension(name).ToLowerInvariant()}";
        var file = Path.Combine(folder, stored);
        long written;
        await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(output, ct);
            written = output.Length;
        }

        if (options.MaxUploadBytes > 0 && written > options.MaxUploadBytes)
        {
            File.Delete(file);
            return Text.T("Import.Error.TooLarge", name, Ui.Bytes(options.MaxUploadBytes));
        }

        // the folder the file came from (an upload of a folder) helps to name its album
        var relative = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var from = AlbumNames.ParentOf(relative);
        db.ImportItems.Add(new ImportItem
        {
            ImportSessionId = session.Id, SourcePath = stored, FileName = name.Length > 255 ? name[..255] : name, Size = written, FileModified = DateTime.UtcNow,
            SourceFolder = from.Length == 0 ? null : (from.Length > 2000 ? from[..2000] : from),
        });
        session.TotalCount++;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>The uploads are complete: the analysis starts (in the background).</summary>
    public static async Task<bool> StartAnalysisAsync(AppDbContext db, long userId, Guid token, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(s => s.Token == token && s.UserId == userId && s.State == ImportState.Collecting, ct);
        if (session is null)
        {
            return false;
        }

        session.State = ImportState.Analyzing;
        session.Message = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static async Task<ImportView?> GetAsync(AppDbContext db, long userId, Guid token, CancellationToken ct = default)
    {
        var s = await db.ImportSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Token == token && x.UserId == userId, ct);
        if (s is null)
        {
            return null;
        }

        var sourceName = s.SourceStorageId is { } source ? await db.Storages.Where(x => x.Id == source).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var items = await db.ImportItems.AsNoTracking().Where(i => i.ImportSessionId == s.Id).OrderBy(i => i.TakenDate).ThenBy(i => i.FileName).ToListAsync(ct);
        var groups = await db.ImportGroups.AsNoTracking().Where(g => g.ImportSessionId == s.Id).OrderBy(g => g.Position).ToListAsync(ct);
        var byGroup = items.Where(i => i.ImportGroupId != null).ToLookup(i => i.ImportGroupId!.Value);
        return new ImportView(s.Id, s.Token, s.Source, sourceName, s.SourcePath, s.MoveFiles, s.State, s.TargetStorageId, s.TargetFolder, s.TotalCount, s.DoneCount, s.ImportedCount,
            s.SkippedCount, s.FailedCount, s.Message, s.CreateDate, s.FinishedDate,
            groups.Select(g => new ImportGroupView(g.Id, g.Position, g.Name, g.StartDate, g.EndDate, g.Place, g.Reason, g.Include, g.ExistingAlbumId, byGroup[g.Id].Select(View).ToList())).ToList(),
            items.Where(i => i.DuplicateOfMediaId != null || i.DuplicateOfItemId != null).Select(View).ToList(),
            items.Where(i => i.ImportGroupId == null && i.DuplicateOfMediaId == null && i.DuplicateOfItemId == null && i.State is ImportItemState.Failed).Select(View).ToList());
    }

    /// <summary>The imports of a person, newest first (the page History).</summary>
    public static async Task<IReadOnlyList<ImportRow>> HistoryAsync(AppDbContext db, long userId, int take, CancellationToken ct = default)
    {
        var rows = await db.ImportSessions.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.CreateDate).Take(take)
            .Select(s => new { s.Token, s.Source, s.SourceStorageId, s.State, s.TotalCount, s.ImportedCount, s.SkippedCount, s.FailedCount, s.CreateDate, s.FinishedDate })
            .ToListAsync(ct);
        var ids = rows.Where(r => r.SourceStorageId != null).Select(r => r.SourceStorageId!.Value).Distinct().ToList();
        var names = await db.Storages.IgnoreQueryFilters().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return rows.Select(r => new ImportRow(r.Token, r.Source, r.SourceStorageId is { } id ? names.GetValueOrDefault(id) : null, r.State, r.TotalCount, r.ImportedCount, r.SkippedCount,
            r.FailedCount, r.CreateDate, r.FinishedDate)).ToList();
    }

    /// <summary>The albums an import could go into: those of the storages the person may write, with the dates near the import first.</summary>
    public static async Task<IReadOnlyList<AlbumChoice>> AlbumChoicesAsync(AppDbContext db, long userId, ImportView view, CancellationToken ct = default)
    {
        var writable = await StorageAccess.WritableIdsAsync(db, userId, ct);
        var dates = view.Groups.Where(g => g.StartDate != null).Select(g => g.StartDate!.Value.Date).ToList();
        var from = dates.Count == 0 ? DateTime.MinValue : dates.Min().AddDays(-7);
        var to = dates.Count == 0 ? DateTime.MaxValue : dates.Max().AddDays(8);
        var near = await db.Albums.AsNoTracking().Where(a => writable.Contains(a.StorageId) && a.Path != string.Empty
                && ((a.StartDate != null && a.StartDate >= from && a.StartDate < to) || view.Groups.Select(g => g.ExistingAlbumId).Contains(a.Id)))
            .OrderBy(a => a.StartDate).Take(200).Select(a => new AlbumChoice(a.Id, a.Name, a.StorageId, a.StartDate)).ToListAsync(ct);
        return near;
    }

    /// <summary>Saves the decisions of the review page; with <paramref name="start"/> the import runs (in the background). Answers the errors in words.</summary>
    public static async Task<(bool Found, List<FieldError> Errors)> DecideAsync(AppDbContext db, long userId, Guid token, ImportDecision decision, bool start, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(s => s.Token == token && s.UserId == userId && s.State == ImportState.Review, ct);
        if (session is null)
        {
            return (false, []);
        }

        var errors = new List<FieldError>();
        var writable = await StorageAccess.WritableIdsAsync(db, userId, ct);
        var groups = await db.ImportGroups.Where(g => g.ImportSessionId == session.Id).ToListAsync(ct);
        string folder = string.Empty;
        try
        {
            folder = PathGuard.Normalize(decision.TargetFolder ?? string.Empty);
        }
        catch (StorageException)
        {
            errors.Add(new FieldError("TargetFolder", Text.T("Import.Error.Folder")));
        }

        var needsTarget = false;
        foreach (var input in decision.Groups)
        {
            var group = groups.FirstOrDefault(g => g.Id == input.Id);
            if (group is null)
            {
                continue;
            }

            group.Include = input.Include;
            group.ExistingAlbumId = null;
            if (input.ExistingAlbumId is { } albumId)
            {
                if (await db.Albums.AnyAsync(a => a.Id == albumId && writable.Contains(a.StorageId) && a.Path != string.Empty, ct))
                {
                    group.ExistingAlbumId = albumId;
                }
                else
                {
                    errors.Add(new FieldError($"Group{group.Id}", Text.T("Import.Error.Album")));
                }
            }

            var name = AlbumNames.Clean(input.Name ?? string.Empty);
            if (group.Include && group.ExistingAlbumId is null)
            {
                needsTarget = true;
                if (name.Length == 0)
                {
                    errors.Add(new FieldError($"Group{group.Id}", Text.T("Import.Error.Name")));
                }
            }

            group.Name = name.Length == 0 ? group.Name : name;
        }

        if (needsTarget && (decision.TargetStorageId is not { } target || !writable.Contains(target) || !(await db.Storages.AnyAsync(s => s.Id == target && !s.IsReadOnly, ct))))
        {
            errors.Add(new FieldError("TargetStorageId", Text.T("Import.Error.Target")));
        }

        session.TargetStorageId = decision.TargetStorageId;
        session.TargetFolder = folder;
        if (errors.Count == 0 && start)
        {
            if (!groups.Any(g => g.Include))
            {
                errors.Add(new FieldError("Groups", Text.T("Import.Error.Nothing")));
            }
            else
            {
                session.State = ImportState.Importing;
                session.DoneCount = 0;
            }
        }

        await db.SaveChangesAsync(ct);
        return (true, errors);
    }

    /// <summary>Stops an import that is not running and removes its uploads (an import that is done stays in the history).</summary>
    public static async Task<bool> CancelAsync(AppDbContext db, long userId, Guid token, string dataDirectory, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(s => s.Token == token && s.UserId == userId, ct);
        if (session is null || session.State is ImportState.Importing or ImportState.Done)
        {
            return false;
        }

        session.State = ImportState.Cancelled;
        session.FinishedDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        DeleteStaging(dataDirectory, token);
        return true;
    }

    public static void DeleteStaging(string dataDirectory, Guid token)
    {
        var folder = StagingFolder(dataDirectory, token);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // the housekeeping takes it later
        }
    }

    /// <summary>
    /// The suggestion: the files that are no duplicates, ordered by their date, become groups. A new group starts when more than <see cref="LibraryOptions.ImportGapHours"/>
    /// lie between two photos on different days, or when two photos in a row were taken more than <see cref="LibraryOptions.ImportDistanceKm"/> apart. Screenshots
    /// become a group per month, files without a date one group of their own.
    /// </summary>
    public static List<(ImportGroup Group, List<ImportItem> Items)> Suggest(IReadOnlyList<ImportItem> items, LibraryOptions options, DateOnly today)
    {
        var result = new List<(ImportGroup, List<ImportItem>)>();
        var usable = items.Where(i => i.State == ImportItemState.Ready && i.DuplicateOfMediaId is null && i.DuplicateOfItemId is null).ToList();
        var screenshots = usable.Where(i => (i.Traits & MediaTraits.Screenshot) != 0 && i.TakenDate is not null).ToList();
        var undated = usable.Where(i => i.TakenDate is null || i.DateSource == DateSource.FileDate).Except(screenshots).ToList();
        var dated = usable.Except(screenshots).Except(undated).OrderBy(i => i.TakenDate).ToList();

        var current = new List<ImportItem>();
        void Close()
        {
            if (current.Count > 0)
            {
                result.Add((Describe(current, "time", options), current));
                current = [];
            }
        }

        foreach (var item in dated)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                var gap = item.TakenDate!.Value - last.TakenDate!.Value;
                var otherDay = item.TakenDate.Value.Date != last.TakenDate.Value.Date;
                var far = item.Latitude is { } la && item.Longitude is { } lo && LastPosition(current) is { } p
                    && GeoPlaces.DistanceKm(p.Lat, p.Lon, la, lo) > options.ImportDistanceKm && gap > TimeSpan.FromHours(1);
                var otherFolder = item.SourceFolder != last.SourceFolder;
                if ((gap > TimeSpan.FromHours(options.ImportGapHours) && otherDay) || far || otherFolder)
                {
                    Close();
                }
            }

            current.Add(item);
        }

        Close();
        foreach (var month in screenshots.GroupBy(i => (i.TakenDate!.Value.Year, i.TakenDate.Value.Month)).OrderBy(g => g.Key))
        {
            var list = month.OrderBy(i => i.TakenDate).ToList();
            var group = Describe(list, "screenshots", options);
            group.Name = AlbumNames.Compose(new DateOnly(month.Key.Year, month.Key.Month, 1), Text.T("Import.Screenshots"), options.AlbumDateFormat);
            result.Add((group, list));
        }

        if (undated.Count > 0)
        {
            var group = Describe(undated, "nodate", options);
            group.Name = AlbumNames.Compose(today, Text.T("Import.Undated"), options.AlbumDateFormat);
            result.Add((group, undated));
        }

        for (var i = 0; i < result.Count; i++)
        {
            result[i].Item1.Position = i + 1;
        }

        return result;
    }

    private static (double Lat, double Lon)? LastPosition(List<ImportItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Latitude is { } lat && items[i].Longitude is { } lon)
            {
                return (lat, lon);
            }
        }

        return null;
    }

    /// <summary>The name of a group: the date of its first photo and the folder the files came from, else the town most photos were taken in.</summary>
    private static ImportGroup Describe(List<ImportItem> items, string reason, LibraryOptions options)
    {
        var start = items.Where(i => i.TakenDate != null).Select(i => i.TakenDate!.Value).DefaultIfEmpty().Min();
        var end = items.Where(i => i.TakenDate != null).Select(i => i.TakenDate!.Value).DefaultIfEmpty().Max();
        var place = items.Where(i => i.City != null).GroupBy(i => i.City).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? items.Where(i => i.Country != null).GroupBy(i => i.Country).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var folder = items.Select(i => i.SourceFolder).Distinct().ToList() is [{ } only] ? AlbumNames.FolderName(only) : null;
        var title = folder is { Length: > 0 } ? AlbumNames.Parse(folder).Title : place;
        var date = start == default ? (DateOnly?)null : DateOnly.FromDateTime(start);
        var folderDate = folder is { Length: > 0 } ? AlbumNames.Parse(folder).Date : null;
        return new ImportGroup
        {
            Name = AlbumNames.Compose(folderDate ?? date, title, options.AlbumDateFormat),
            StartDate = start == default ? null : start,
            EndDate = end == default ? null : end,
            Place = place,
            Reason = folder is { Length: > 0 } && reason == "time" ? "folder" : place is not null && reason == "time" ? "place" : reason,
            ItemCount = items.Count,
        };
    }

    internal static ImportItemView View(ImportItem i) => new(i.Id, i.FileName, i.SourcePath, i.Size, i.Type, i.Traits, i.TakenDate, i.DateSource, i.City, i.Country, i.CameraModel,
        i.ContentHash, i.HasThumb, i.DuplicateOfMediaId, i.DuplicateOfItemId, i.State, i.Error, i.TargetPath, i.Width, i.Height);
}

using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>
/// Does the work of the imports in the background: it looks every few seconds for an import that waits for its analysis or its run (so an import goes on after a
/// restart of the app), one at a time. The analysis reads every file (<see cref="MediaIndexer"/>, thumbnails included), marks the duplicates and suggests the albums
/// (<see cref="ImportService.Suggest"/>); the run copies or moves the files of every chosen group into its folder, never over a file that is there (<c>name (2).jpg</c>),
/// and scans the target storages afterwards.
/// </summary>
public sealed class ImportWorker(IServiceScopeFactory scopes, StorageConnections connections, MediaIndexer indexer, LibraryScanner scanner, LibrarySettings settings,
    IConfiguration configuration, ILogger<ImportWorker> log) : BackgroundService
{
    private readonly string _data = configuration["DataDirectory"] ?? "/data";
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Something waits (a new analysis or run): look now instead of in a few seconds.</summary>
    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                long? next;
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    next = await db.ImportSessions.Where(s => s.State == ImportState.Analyzing || s.State == ImportState.Importing)
                        .OrderBy(s => s.UpdateDate).Select(s => (long?)s.Id).FirstOrDefaultAsync(stoppingToken);
                }

                if (next is { } id)
                {
                    try
                    {
                        await RunAsync(id, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Import {Id} failed.", id);
                        await FailAsync(id, StorageErrors.Message(ex), stoppingToken);
                    }

                    continue;
                }

                await _wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    public async Task RunAsync(long id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.ImportSessions.FirstAsync(s => s.Id == id, ct);
        if (session.State == ImportState.Analyzing)
        {
            await AnalyzeAsync(db, session, ct);
        }
        else if (session.State == ImportState.Importing)
        {
            await ImportAsync(db, session, ct);
        }
    }

    // ---- the analysis -------------------------------------------------------------------------------------------------------------------

    private async Task AnalyzeAsync(AppDbContext db, ImportSession session, CancellationToken ct)
    {
        IStorageProvider? source = null;
        if (session.Source == ImportSource.Storage)
        {
            var storage = await db.Storages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == session.SourceStorageId, ct)
                ?? throw new StorageException(StorageError.Worded, Text.T("Import.Error.Source"));
            source = connections.Create(storage);
            if (!await db.ImportItems.AnyAsync(i => i.ImportSessionId == session.Id, ct))
            {
                await ListSourceAsync(db, session, source, ct);
            }
        }

        var folder = ImportService.StagingFolder(_data, session.Token);
        var pending = await db.ImportItems.Where(i => i.ImportSessionId == session.Id && i.State == ImportItemState.Pending).ToListAsync(ct);
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var facts = source is null
                    ? await indexer.IndexLocalAsync(Path.Combine(folder, item.SourcePath), item.FileName, null, ct)
                    : await indexer.IndexAsync(source, item.SourcePath, item.Size, item.FileModified, AlbumNames.Parse(item.SourceFolder is null ? string.Empty : AlbumNames.FolderName(item.SourceFolder)).Date, ct);
                item.Type = facts.Type;
                item.Traits = facts.Traits;
                item.ContentHash = facts.ContentHash;
                item.Width = facts.Width;
                item.Height = facts.Height;
                item.TakenDate = facts.TakenDate;
                item.DateSource = facts.DateSource;
                item.Latitude = facts.Latitude;
                item.Longitude = facts.Longitude;
                item.City = facts.Place?.City;
                item.Country = facts.Place?.Country;
                item.CameraModel = facts.CameraModel;
                item.HasThumb = facts.ThumbReady;
                item.State = ImportItemState.Ready;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.State = ImportItemState.Failed;
                item.Error = Short(StorageErrors.Message(ex));
            }

            session.DoneCount++;
            await db.SaveChangesAsync(ct);
        }

        // the duplicates: in the library already (among the photos the person sees), or twice in this import
        var items = await db.ImportItems.Where(i => i.ImportSessionId == session.Id).OrderBy(i => i.Id).ToListAsync(ct);
        var visible = await StorageAccess.VisibleIdsAsync(db, session.UserId, ct);
        var hashes = items.Where(i => i.ContentHash.Length > 0).Select(i => i.ContentHash).Distinct().ToList();
        var known = new Dictionary<(string, long), List<(long Id, long StorageId, string Path)>>();
        foreach (var chunk in hashes.Chunk(500))
        {
            var rows = await db.Media.Where(m => chunk.Contains(m.ContentHash) && visible.Contains(m.StorageId))
                .Select(m => new { m.ContentHash, m.Size, m.Id, m.StorageId, m.Path }).ToListAsync(ct);
            foreach (var r in rows)
            {
                if (!known.TryGetValue((r.ContentHash, r.Size), out var list))
                {
                    known[(r.ContentHash, r.Size)] = list = [];
                }

                list.Add((r.Id, r.StorageId, r.Path));
            }
        }

        var first = new Dictionary<(string, long), long>();
        foreach (var item in items.Where(i => i.State == ImportItemState.Ready))
        {
            var key = (item.ContentHash, item.Size);

            // a file of an inbox inside a library is in the library already – as itself: that is no duplicate (sorting the inbox into albums moves it)
            var other = known.TryGetValue(key, out var copies)
                ? copies.FirstOrDefault(c => !(session.Source == ImportSource.Storage && c.StorageId == session.SourceStorageId && c.Path == item.SourcePath))
                : default;
            item.DuplicateOfMediaId = other.Id > 0 ? other.Id : null;
            item.DuplicateOfItemId = item.DuplicateOfMediaId is null && first.TryGetValue(key, out var itemId) ? itemId : null;
            first.TryAdd(key, item.Id);
        }

        // the suggestion
        await db.ImportItems.Where(i => i.ImportSessionId == session.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.ImportGroupId, (long?)null), ct);
        await db.ImportGroups.Where(g => g.ImportSessionId == session.Id).ExecuteDeleteAsync(ct);
        var options = settings.Current;
        var suggestion = ImportService.Suggest(items, options, DateOnly.FromDateTime(DateTime.Now));
        foreach (var (group, members) in suggestion)
        {
            group.ImportSessionId = session.Id;
            db.ImportGroups.Add(group);
            await db.SaveChangesAsync(ct);
            foreach (var member in members)
            {
                member.ImportGroupId = group.Id;
            }

            group.ExistingAlbumId = await MatchingAlbumAsync(db, session, group, ct);
        }

        session.TotalCount = items.Count;
        session.SkippedCount = items.Count(i => i.DuplicateOfMediaId != null || i.DuplicateOfItemId != null);
        session.FailedCount = items.Count(i => i.State == ImportItemState.Failed);
        session.State = ImportState.Review;
        session.Message = null;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Import {Token}: {Count} files analysed, {Groups} albums suggested.", session.Token, items.Count, suggestion.Count);
    }

    /// <summary>The files of the source folder and its subfolders (the NAS's own folders left out).</summary>
    private static async Task ListSourceAsync(AppDbContext db, ImportSession session, IStorageProvider source, CancellationToken ct)
    {
        var open = new Stack<string>();
        open.Push(session.SourcePath ?? string.Empty);
        var count = 0;
        while (open.TryPop(out var folder) && count < 50000)
        {
            foreach (var entry in await source.ListAsync(folder, ct))
            {
                if (entry.IsFolder)
                {
                    if (MediaFormats.IsScanned(entry.Name))
                    {
                        open.Push(entry.Path);
                    }
                }
                else if (MediaFormats.IsMedia(entry.Name))
                {
                    var parent = AlbumNames.ParentOf(entry.Path);
                    db.ImportItems.Add(new ImportItem
                    {
                        ImportSessionId = session.Id, SourcePath = entry.Path, FileName = entry.Name, Size = entry.Size,
                        FileModified = DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc),
                        SourceFolder = parent == (session.SourcePath ?? string.Empty) ? null : parent,
                    });
                    count++;
                }
            }
        }

        session.TotalCount = count;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An album of the target storage with the same date as the group (only when exactly one has it).</summary>
    private static async Task<long?> MatchingAlbumAsync(AppDbContext db, ImportSession session, ImportGroup group, CancellationToken ct)
    {
        if (session.TargetStorageId is not { } target || group.StartDate is not { } start || group.Reason is "screenshots" or "nodate")
        {
            return null;
        }

        var day = DateOnly.FromDateTime(start);
        var albums = await db.Albums.Where(a => a.StorageId == target && a.AlbumDate == day && a.Path != string.Empty).Select(a => a.Id).Take(2).ToListAsync(ct);
        return albums.Count == 1 ? albums[0] : null;
    }

    // ---- the run ---------------------------------------------------------------------------------------------------------------------------

    private async Task ImportAsync(AppDbContext db, ImportSession session, CancellationToken ct)
    {
        var writable = await StorageAccess.WritableIdsAsync(db, session.UserId, ct);
        IStorageProvider? source = null;
        Storage? sourceStorage = null;
        if (session.Source == ImportSource.Storage)
        {
            sourceStorage = await db.Storages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == session.SourceStorageId, ct)
                ?? throw new StorageException(StorageError.Worded, Text.T("Import.Error.Source"));
            source = connections.Create(sourceStorage);
        }

        var staging = ImportService.StagingFolder(_data, session.Token);
        var groups = await db.ImportGroups.Where(g => g.ImportSessionId == session.Id && g.Include).OrderBy(g => g.Position).ToListAsync(ct);
        var targets = new HashSet<long>();
        var options = settings.Current;
        foreach (var group in groups)
        {
            // where the group goes: an album that exists, or a new folder in the target storage
            Storage? storage;
            string folder;
            if (group.ExistingAlbumId is { } albumId && await db.Albums.AsNoTracking().Include(a => a.Storage).FirstOrDefaultAsync(a => a.Id == albumId, ct) is { Storage: not null } album
                && writable.Contains(album.StorageId))
            {
                storage = album.Storage;
                folder = album.Path;
            }
            else
            {
                storage = session.TargetStorageId is { } t && writable.Contains(t) ? await db.Storages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == t, ct) : null;
                var name = AlbumNames.Clean(group.Name);
                if (name.Length == 0)
                {
                    name = AlbumNames.Compose(group.StartDate is { } d ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(DateTime.Now), Text.T("Import.Title"), options.AlbumDateFormat);
                }

                folder = session.TargetFolder.Length == 0 ? name : $"{session.TargetFolder}/{name}";
            }

            if (storage is null || storage.IsReadOnly)
            {
                throw new StorageException(StorageError.Worded, Text.T("Import.Error.Target"));
            }

            var target = connections.Create(storage);
            await StorageFolders.EnsureAsync(target, folder, ct);
            targets.Add(storage.Id);
            var items = await db.ImportItems.Where(i => i.ImportGroupId == group.Id && i.State == ImportItemState.Ready).OrderBy(i => i.TakenDate).ToListAsync(ct);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var path = await FreeNameAsync(target, folder, item.FileName, ct);
                    var sameStorage = sourceStorage is not null && sourceStorage.Id == storage.Id;
                    if (session.MoveFiles && sameStorage && source is not null)
                    {
                        await source.MoveAsync(item.SourcePath, path, overwrite: false, ct);
                    }
                    else
                    {
                        await using (var input = source is null
                            ? new FileStream(Path.Combine(staging, item.SourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
                            : await source.OpenReadAsync(item.SourcePath, 0, ct))
                        {
                            await target.WriteAsync(path, input, item.Size, overwrite: false, ct);
                        }

                        if (session.MoveFiles && source is not null)
                        {
                            await source.DeleteAsync(item.SourcePath, ct);
                        }
                    }

                    item.State = ImportItemState.Imported;
                    item.TargetPath = path;
                    session.ImportedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.State = ImportItemState.Failed;
                    item.Error = Short(StorageErrors.Message(ex));
                    session.FailedCount++;
                }

                session.DoneCount++;
                await db.SaveChangesAsync(ct);
            }

            ActivityLog.Add(db, session.UserId, "import", group.Name, $"{items.Count(i => i.State == ImportItemState.Imported)} files", storageId: storage.Id);
        }

        session.State = ImportState.Done;
        session.FinishedDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (session.Source == ImportSource.Upload)
        {
            ImportService.DeleteStaging(_data, session.Token);
        }

        foreach (var storageId in targets)
        {
            scanner.Request(storageId);
        }

        log.LogInformation("Import {Token} done: {Imported} imported, {Failed} failed.", session.Token, session.ImportedCount, session.FailedCount);
    }

    /// <summary>A name that is free in the folder: <c>IMG_0001.jpg</c>, else <c>IMG_0001 (2).jpg</c> …</summary>
    private static async Task<string> FreeNameAsync(IStorageProvider target, string folder, string fileName, CancellationToken ct)
    {
        var name = AlbumNames.Clean(fileName);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? name : $"{stem} ({n}){extension}";
            var path = folder.Length == 0 ? candidate : $"{folder}/{candidate}";
            if (await target.GetAsync(path, ct) is null)
            {
                return path;
            }
        }

        throw new StorageException(StorageError.Conflict);
    }

    private async Task FailAsync(long id, string message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.ImportSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return;
        }

        // an analysis that failed can be looked at (what was read is there); a run that failed stops
        session.State = ImportState.Failed;
        session.Message = Short(message);
        session.FinishedDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string Short(string text) => text.Length > 500 ? text[..500] : text;
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>How far the scan of one storage is (in memory; the page Status of the administration shows it).</summary>
public sealed class ScanProgress
{
    public long StorageId { get; init; }

    public string StorageName { get; init; } = string.Empty;

    /// <summary>queued, listing, indexing, done, failed.</summary>
    public string Phase { get; set; } = "queued";

    public int Folders { get; set; }

    public int Files { get; set; }

    public int ToIndex { get; set; }

    public int Indexed;

    public int Failed;

    public int Added;

    public int Removed { get; set; }

    public string? Current { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public string? Result { get; set; }

    public bool Running => Phase is "listing" or "indexing";
}

/// <summary>
/// Keeps the library in step with its storages ("sync"): every library storage is scanned when its interval has passed (and whenever somebody asks). A scan lists the
/// folders (without the NAS's own), adds new files at once (so a restart goes on where it stopped), then reads every new or changed file (<see cref="MediaIndexer"/>,
/// newest first, three at a time). A file that moved inside the storage is found by its content and keeps its record (favourites and lists stay). Files that are gone
/// are removed from the library, folders that are gone lose their album. Live Photos (a photo and a short video of the same name) become one item.
/// </summary>
public sealed class LibraryScanner(IServiceScopeFactory scopes, StorageConnections connections, MediaIndexer indexer, ILogger<LibraryScanner> log) : BackgroundService
{
    private const int Parallelism = 3;

    /// <summary>A video of a Live Photo is at most this long.</summary>
    private const int LiveVideoMs = 5000;

    private readonly Channel<long> _requests = Channel.CreateUnbounded<long>();
    private readonly ConcurrentDictionary<long, ScanProgress> _progress = new();
    private readonly ConcurrentDictionary<long, byte> _queued = new();

    /// <summary>The scans of this run of the app (running, queued, finished).</summary>
    public IReadOnlyList<ScanProgress> Progress => _progress.Values.OrderByDescending(p => p.Running).ThenByDescending(p => p.StartedUtc).ToList();

    public ScanProgress? ProgressOf(long storageId) => _progress.GetValueOrDefault(storageId);

    /// <summary>Asks for a scan of a storage soon (a second request while it waits changes nothing).</summary>
    public void Request(long storageId)
    {
        if (_queued.TryAdd(storageId, 0))
        {
            if (!_progress.TryGetValue(storageId, out var current) || !current.Running)
            {
                _progress[storageId] = new ScanProgress { StorageId = storageId, StorageName = current?.StorageName ?? string.Empty };
            }

            _requests.Writer.TryWrite(storageId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var id in await DueAsync(stoppingToken))
                {
                    Request(id);
                }

                // wait for a request, at most a minute (then look at the intervals again)
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    await _requests.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    continue;
                }

                while (_requests.Reader.TryRead(out var storageId))
                {
                    _queued.TryRemove(storageId, out _);
                    try
                    {
                        await ScanAsync(storageId, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "The scan of storage {Storage} failed.", storageId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    /// <summary>The library storages whose interval has passed.</summary>
    private async Task<List<long>> DueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var storages = await db.Storages.AsNoTracking().Where(s => s.IsActive && s.IsLibrary).Select(s => new { s.Id, s.ScanIntervalMinutes, s.LastScanDate }).ToListAsync(ct);
        var pending = await db.Media.Where(m => m.ThumbState == ThumbState.Pending).Select(m => m.StorageId).Distinct().ToListAsync(ct);
        return storages
            .Where(s => s.LastScanDate is null || (s.ScanIntervalMinutes > 0 && s.LastScanDate.Value.AddMinutes(s.ScanIntervalMinutes) <= now) || pending.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>Scans one storage now (the background calls it; the tests too).</summary>
    public async Task<ScanProgress> ScanAsync(long storageId, CancellationToken ct)
    {
        Storage? storage;
        using (var scope = scopes.CreateScope())
        {
            storage = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Storages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storageId, ct);
        }

        var progress = new ScanProgress { StorageId = storageId, StorageName = storage?.Name ?? string.Empty, Phase = "listing" };
        _progress[storageId] = progress;
        if (storage is null || !storage.IsActive || !storage.IsLibrary)
        {
            progress.Phase = "done";
            progress.FinishedUtc = DateTime.UtcNow;
            return progress;
        }

        try
        {
            await RunAsync(storage, progress, ct);
            progress.Phase = "done";
            progress.Result = Text.T("Scan.Result", progress.Files, progress.Added, progress.Removed, progress.Indexed, progress.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            progress.Phase = "failed";
            progress.Result = StorageErrors.Message(ex);
            log.LogWarning("The scan of {Storage} failed: {Message}", storage.Name, progress.Result);
        }

        progress.FinishedUtc = DateTime.UtcNow;
        progress.Current = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Storages.Where(s => s.Id == storageId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastScanDate, DateTime.UtcNow)
                .SetProperty(s => s.LastScanResult, progress.Result), ct);
            if (progress.Added > 0 || progress.Removed > 0 || progress.Phase == "failed")
            {
                ActivityLog.Add(db, null, "scan", storage.Name, progress.Result, storageId: storageId);
                await db.SaveChangesAsync(ct);
            }
        }

        return progress;
    }

    private async Task RunAsync(Storage storage, ScanProgress progress, CancellationToken ct)
    {
        var provider = connections.Create(storage);
        await provider.CheckAsync(ct);
        connections.Seen(storage.Id, true);

        // ---- 1. list every folder ------------------------------------------------------------------------------------------------
        var excluded = MediaFormats.ParseExcluded(storage.ExcludeFolders);
        var files = new Dictionary<string, StorageEntry>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        var open = new Stack<string>();
        open.Push(string.Empty);
        while (open.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            progress.Current = folder;
            IReadOnlyList<StorageEntry> entries;
            try
            {
                entries = await provider.ListAsync(folder, ct);
            }
            catch (StorageException ex) when (folder.Length > 0 && ex.Code is StorageError.AccessDenied or StorageError.NotFound)
            {
                continue; // a folder without the right to read it is left out, the rest is scanned
            }

            foreach (var entry in entries)
            {
                if (entry.IsFolder)
                {
                    if (MediaFormats.IsScanned(entry.Name, excluded))
                    {
                        folders.Add(entry.Path);
                        open.Push(entry.Path);
                    }
                }
                else if (MediaFormats.IsMedia(entry.Name))
                {
                    files[entry.Path] = entry;
                }
            }

            progress.Folders = folders.Count;
            progress.Files = files.Count;
        }

        // ---- 2. the albums: every folder with photos, and those known that still exist ----------------------------------------
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var albums = await db.Albums.Where(a => a.StorageId == storage.Id).ToListAsync(ct);
        var albumByPath = albums.ToDictionary(a => a.Path, StringComparer.Ordinal);
        foreach (var folder in files.Keys.Select(AlbumNames.ParentOf).Distinct(StringComparer.Ordinal))
        {
            if (!albumByPath.ContainsKey(folder))
            {
                var name = folder.Length == 0 ? storage.Name : AlbumNames.FolderName(folder);
                var (date, title) = AlbumNames.Parse(name);
                var album = new Album { StorageId = storage.Id, Path = folder, Name = name, Title = title, AlbumDate = date };
                db.Albums.Add(album);
                albumByPath[folder] = album;
            }
        }

        foreach (var gone in albums.Where(a => !folders.Contains(a.Path)))
        {
            gone.UpdateState = SoftEntity.Deleted;
        }

        await db.SaveChangesAsync(ct);

        // ---- 3. what is new, changed, gone ----------------------------------------------------------------------------------------
        var known = await db.Media.AsNoTracking().Where(m => m.StorageId == storage.Id)
            .Select(m => new Known(m.Id, m.Path, m.Size, m.FileModified, m.ContentHash, m.ThumbState)).ToListAsync(ct);
        var knownByPath = known.ToDictionary(k => k.Path, StringComparer.Ordinal);
        var missing = known.Where(k => !files.ContainsKey(k.Path)).ToList();
        var missingSizes = missing.Select(m => m.Size).ToHashSet();
        var candidates = new List<StorageEntry>();
        var fresh = new List<Media>();
        foreach (var (path, entry) in files)
        {
            if (knownByPath.ContainsKey(path))
            {
                continue;
            }

            if (missingSizes.Contains(entry.Size))
            {
                candidates.Add(entry); // perhaps a file that moved: read it first and compare
                continue;
            }

            var format = MediaFormats.Of(entry.Name)!;
            fresh.Add(new Media
            {
                StorageId = storage.Id,
                AlbumId = albumByPath[AlbumNames.ParentOf(path)].Id,
                Path = path,
                FileName = entry.Name,
                Type = format.Type,
                MimeType = format.MimeType,
                Size = entry.Size,
                FileModified = DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc),
                SortDate = MetadataReader.ToLocal(entry.Modified),
                ThumbState = ThumbState.Pending,
            });
        }

        foreach (var chunk in fresh.Chunk(500))
        {
            db.Media.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        progress.Added = fresh.Count;

        // ---- 4. read what is new or changed (newest first) -------------------------------------------------------------------------
        var changed = known.Where(k => files.TryGetValue(k.Path, out var e) && (e.Size != k.Size || Math.Abs((e.Modified - k.FileModified).TotalSeconds) > 2)).Select(k => k.Path);
        var pending = await db.Media.AsNoTracking().Where(m => m.StorageId == storage.Id && m.ThumbState == ThumbState.Pending).OrderByDescending(m => m.SortDate).Select(m => m.Path).ToListAsync(ct);
        var work = pending.Concat(changed).Distinct(StringComparer.Ordinal).Where(files.ContainsKey).Select(p => files[p]).Concat(candidates).ToList();
        progress.ToIndex = work.Count;
        progress.Phase = "indexing";
        var moved = new ConcurrentDictionary<long, Known>(missing.ToDictionary(m => m.Id));
        var dates = albumByPath.Values.ToDictionary(a => a.Path, a => a.AlbumDate, StringComparer.Ordinal);
        var ids = albumByPath.Values.ToDictionary(a => a.Path, a => a.Id, StringComparer.Ordinal);

        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (entry, token) =>
        {
            progress.Current = entry.Path;
            var folder = AlbumNames.ParentOf(entry.Path);
            try
            {
                var facts = await indexer.IndexAsync(provider, entry.Path, entry.Size, entry.Modified, dates.GetValueOrDefault(folder), token);
                await SaveAsync(storage.Id, ids[folder], entry, facts, moved, progress, token);
                Interlocked.Increment(ref progress.Indexed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Interlocked.Increment(ref progress.Failed);
                log.LogInformation("{Path} of {Storage} could not be read: {Message}", entry.Path, storage.Name, ex.Message);
                await MarkFailedAsync(storage.Id, entry.Path, StorageErrors.Message(ex), token);
            }
        });

        // ---- 5. what is gone, the Live Photos, the numbers of the albums -------------------------------------------------------------
        var vanished = moved.Keys.ToList();
        foreach (var chunk in vanished.Chunk(500))
        {
            await db.Media.Where(m => chunk.Contains(m.Id)).ExecuteUpdateAsync(u => u.SetProperty(m => m.UpdateState, SoftEntity.Deleted).SetProperty(m => m.UpdateDate, DateTime.UtcNow), ct);
        }

        progress.Removed = vanished.Count;
        progress.Current = null;
        await PairLivePhotosAsync(db, storage.Id, ct);
        await RecountAlbumsAsync(db, storage.Id, ct);
    }

    private sealed record Known(long Id, string Path, long Size, DateTime FileModified, string ContentHash, ThumbState ThumbState);

    /// <summary>Writes the facts of one file: into its record, into the record of a file that moved here, or as a new record.</summary>
    private async Task SaveAsync(long storageId, long albumId, StorageEntry entry, MediaFacts facts, ConcurrentDictionary<long, Known> moved, ScanProgress progress, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var media = await db.Media.FirstOrDefaultAsync(m => m.StorageId == storageId && m.Path == entry.Path, ct);
        if (media is null)
        {
            var origin = moved.Values.FirstOrDefault(k => k.ContentHash == facts.ContentHash && k.Size == facts.Size);
            if (origin is not null && moved.TryRemove(origin.Id, out _))
            {
                media = await db.Media.FirstOrDefaultAsync(m => m.Id == origin.Id, ct);
            }

            if (media is null)
            {
                media = db.Media.Add(new Media { StorageId = storageId }).Entity;
                Interlocked.Increment(ref progress.Added);
            }
        }

        media.AlbumId = albumId;
        media.Path = entry.Path;
        media.FileName = entry.Name;
        facts.ApplyTo(media);
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkFailedAsync(long storageId, string path, string message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var error = message.Length > 500 ? message[..500] : message;
        await db.Media.Where(m => m.StorageId == storageId && m.Path == path && m.ThumbState == ThumbState.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ThumbState, ThumbState.Failed).SetProperty(m => m.ThumbError, error), ct);
    }

    /// <summary>A photo and a video of at most five seconds with the same name in the same folder (<c>IMG_0001.HEIC</c> + <c>IMG_0001.MOV</c>) are a Live Photo.</summary>
    internal static async Task PairLivePhotosAsync(AppDbContext db, long storageId, CancellationToken ct)
    {
        var rows = await db.Media.Where(m => m.StorageId == storageId)
            .Select(m => new { m.Id, m.Path, m.Type, m.DurationMs, m.Traits, m.LiveVideoId }).ToListAsync(ct);
        var pairs = rows
            .GroupBy(r => Path.ChangeExtension(r.Path, null), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Photo: g.FirstOrDefault(r => r.Type == MediaType.Photo), Video: g.FirstOrDefault(r => r.Type == MediaType.Video && r.DurationMs is null or <= LiveVideoMs)))
            .Where(p => p.Photo is not null && p.Video is not null)
            .ToDictionary(p => p.Photo!.Id, p => p.Video!.Id);
        var videos = pairs.Values.ToHashSet();
        foreach (var r in rows)
        {
            var traits = r.Traits & ~(MediaTraits.LivePhoto | MediaTraits.LiveVideo);
            long? live = null;
            if (pairs.TryGetValue(r.Id, out var video))
            {
                traits |= MediaTraits.LivePhoto;
                live = video;
            }
            else if (videos.Contains(r.Id))
            {
                traits |= MediaTraits.LiveVideo;
            }

            if (traits != r.Traits || live != r.LiveVideoId)
            {
                await db.Media.Where(m => m.Id == r.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.Traits, traits).SetProperty(m => m.LiveVideoId, live), ct);
            }
        }
    }

    /// <summary>The numbers of every album of a storage: how many photos, how many bytes, from when to when (the video part of a Live Photo does not count).</summary>
    internal static async Task RecountAlbumsAsync(AppDbContext db, long storageId, CancellationToken ct)
    {
        var stats = await db.Media.Where(m => m.StorageId == storageId && (m.Traits & MediaTraits.LiveVideo) == 0)
            .GroupBy(m => m.AlbumId)
            .Select(g => new { AlbumId = g.Key, Count = g.Count(), Bytes = g.Sum(m => m.Size), Start = g.Min(m => m.SortDate), End = g.Max(m => m.SortDate) })
            .ToDictionaryAsync(x => x.AlbumId, ct);
        var albums = await db.Albums.Where(a => a.StorageId == storageId).ToListAsync(ct);
        foreach (var album in albums)
        {
            if (stats.TryGetValue(album.Id, out var s))
            {
                (album.MediaCount, album.Bytes, album.StartDate, album.EndDate) = (s.Count, s.Bytes, s.Start, s.End);
            }
            else
            {
                (album.MediaCount, album.Bytes, album.StartDate, album.EndDate) = (0, 0, null, null);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

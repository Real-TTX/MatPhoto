using MatPhoto.Data;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>What happened to photos that were to be moved into the trash.</summary>
public sealed record TrashResult(int Moved, int Refused, IReadOnlyList<string> Errors);

/// <summary>
/// Changes of photos on their storage. MatPhoto never deletes a file for good: "Delete" moves it into the trash folder of its storage
/// (<c>.matphoto-trash/&lt;date&gt;/&lt;the path it had&gt;</c>, never scanned), from where it can be put back by hand. It needs the role Editor on the storage.
/// </summary>
public static class MediaActions
{
    public static async Task<TrashResult> TrashAsync(AppDbContext db, StorageConnections connections, long userId, IReadOnlyCollection<long> mediaIds, CancellationToken ct = default)
    {
        var writable = await StorageAccess.WritableIdsAsync(db, userId, ct);
        var media = await db.Media.Include(m => m.Storage).Where(m => mediaIds.Contains(m.Id)).ToListAsync(ct);
        var refused = mediaIds.Count - media.Count(m => writable.Contains(m.StorageId));
        var errors = new List<string>();
        var moved = 0;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var touched = new HashSet<long>();
        foreach (var group in media.Where(m => writable.Contains(m.StorageId)).GroupBy(m => m.StorageId))
        {
            var storage = group.First().Storage!;
            var provider = connections.Create(storage);
            var items = group.ToList();

            // the video of a Live Photo goes with its photo
            var liveIds = items.Where(m => m.LiveVideoId != null).Select(m => m.LiveVideoId!.Value).ToList();
            items.AddRange(await db.Media.Where(m => liveIds.Contains(m.Id) && !mediaIds.Contains(m.Id)).ToListAsync(ct));
            foreach (var m in items)
            {
                try
                {
                    var target = $"{MediaFormats.TrashFolder}/{stamp}/{m.Path}";
                    await StorageFolders.EnsureAsync(provider, AlbumNames.ParentOf(target), ct);
                    await provider.MoveAsync(m.Path, target, overwrite: false, ct);
                    m.UpdateState = SoftEntity.Deleted;
                    touched.Add(m.StorageId);
                    if (mediaIds.Contains(m.Id))
                    {
                        moved++;
                    }

                    ActivityLog.Add(db, userId, "trash", m.FileName, m.Path, storageId: m.StorageId, albumId: m.AlbumId, mediaId: m.Id);
                }
                catch (StorageException ex) when (ex.Code is StorageError.NotFound)
                {
                    m.UpdateState = SoftEntity.Deleted; // gone already: the library forgets it
                    moved++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{m.FileName}: {StorageErrors.Message(ex)}");
                }
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var storageId in touched)
        {
            await LibraryScanner.RecountAlbumsAsync(db, storageId, ct);
        }

        return new TrashResult(moved, refused, errors);
    }
}

using MatPhoto.Data;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatPhoto.Services.Library;

/// <summary>The two pictures made of every photo: the tile of the grid and the preview of the viewer.</summary>
public enum ThumbKind
{
    /// <summary>The tile of the grid (<see cref="LibraryOptions.ThumbnailHeight"/> high).</summary>
    Thumb,

    /// <summary>The picture of the viewer (<see cref="LibraryOptions.PreviewSize"/> on the longer side).</summary>
    Preview,
}

/// <summary>
/// The cache of the thumbnails and previews, keyed by the content of the photo (<see cref="Media.ContentHash"/>): the same photo in two albums has one thumbnail, and a
/// photo that moves keeps it. The cache lies in the data volume (<c>/data/cache/t/ab/&lt;hash&gt;.jpg</c>) or, when the administration chose a storage for it, in a folder
/// of that storage (a NAS). The tiles that were asked for last are also kept in memory (up to 96 MB), so scrolling back costs nothing.
/// </summary>
public sealed class ThumbnailCache(LibrarySettings settings, StorageConnections connections, IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ThumbnailCache> log)
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions { SizeLimit = 96L * 1024 * 1024 });
    private readonly string _localRoot = Path.Combine(configuration["DataDirectory"] ?? "/data", "cache");
    private (long Id, Storage Storage)? _storage;

    /// <summary>The cache folder of the data volume (used when no storage is chosen, and always for the pictures of an import that is not done yet).</summary>
    public string LocalRoot => _localRoot;

    public async Task<byte[]?> ReadAsync(string hash, ThumbKind kind, CancellationToken ct)
    {
        if (!IsHash(hash))
        {
            return null;
        }

        var key = (hash, kind);
        if (_memory.TryGetValue(key, out byte[]? cached))
        {
            return cached;
        }

        byte[]? data;
        try
        {
            data = await ReadStoredAsync(hash, kind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "A thumbnail could not be read from the cache.");
            data = null;
        }

        if (data is not null && kind == ThumbKind.Thumb)
        {
            _memory.Set(key, data, new MemoryCacheEntryOptions { Size = data.Length, SlidingExpiration = TimeSpan.FromHours(1) });
        }

        return data;
    }

    public async Task<bool> ExistsAsync(string hash, ThumbKind kind, CancellationToken ct)
    {
        if (!IsHash(hash))
        {
            return false;
        }

        if (await StorageAsync(ct) is { } storage)
        {
            var provider = connections.Create(storage);
            return await provider.GetAsync(RelativePath(hash, kind), ct) is { IsFolder: false, Size: > 0 };
        }

        return File.Exists(LocalPath(hash, kind));
    }

    public async Task WriteAsync(string hash, ThumbKind kind, byte[] data, CancellationToken ct)
    {
        if (!IsHash(hash))
        {
            throw new ArgumentException("Not a content hash.", nameof(hash));
        }

        if (await StorageAsync(ct) is { } storage)
        {
            var provider = connections.Create(storage);
            using var content = new MemoryStream(data, writable: false);
            await provider.WriteAsync(RelativePath(hash, kind), content, data.Length, overwrite: true, ct);
        }
        else
        {
            var path = LocalPath(hash, kind);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".part";
            await File.WriteAllBytesAsync(temp, data, ct);
            File.Move(temp, path, overwrite: true);
        }

        if (kind == ThumbKind.Thumb)
        {
            _memory.Set((hash, kind), data, new MemoryCacheEntryOptions { Size = data.Length, SlidingExpiration = TimeSpan.FromHours(1) });
        }
    }

    /// <summary>Forgets the storage of the cache (the administration chose another one): the next call reads the setting again.</summary>
    public void Changed()
    {
        _storage = null;
        _memory.Compact(1.0);
    }

    /// <summary>The bytes the cache of the data volume takes (the page System).</summary>
    public long LocalBytes()
    {
        try
        {
            return Directory.Exists(_localRoot) ? new DirectoryInfo(_localRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitLower);

    private async Task<byte[]?> ReadStoredAsync(string hash, ThumbKind kind, CancellationToken ct)
    {
        if (await StorageAsync(ct) is { } storage)
        {
            var provider = connections.Create(storage);
            try
            {
                await using var stream = await provider.OpenReadAsync(RelativePath(hash, kind), 0, ct);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                return buffer.ToArray();
            }
            catch (StorageException e) when (e.Code is StorageError.NotFound)
            {
                return null;
            }
        }

        var path = LocalPath(hash, kind);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    private async Task<Storage?> StorageAsync(CancellationToken ct)
    {
        var id = settings.Current.CacheStorageId;
        if (id is null)
        {
            return null;
        }

        if (_storage is { } known && known.Id == id)
        {
            return known.Storage;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = await db.Storages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);
        if (storage is null)
        {
            return null;
        }

        _storage = (id.Value, storage);
        return storage;
    }

    private string RelativePath(string hash, ThumbKind kind)
        => $"{settings.Current.CacheFolder.Trim('/')}/{Letter(kind)}/{hash[..2]}/{hash}.jpg";

    private string LocalPath(string hash, ThumbKind kind) => Path.Combine(_localRoot, Letter(kind), hash[..2], hash + ".jpg");

    private static string Letter(ThumbKind kind) => kind == ThumbKind.Thumb ? "t" : "p";
}

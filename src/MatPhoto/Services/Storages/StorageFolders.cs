namespace MatPhoto.Services.Storages;

/// <summary>Folders of a storage that must exist before a file is written or moved into them.</summary>
public static class StorageFolders
{
    /// <summary>Makes every missing folder of <paramref name="path"/> (relative, with slashes), one level after the other (not every protocol makes the ones above).</summary>
    public static async Task EnsureAsync(IStorageProvider provider, string path, CancellationToken ct)
    {
        var current = string.Empty;
        foreach (var part in PathGuard.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var entry = await provider.GetAsync(current, ct);
            if (entry is null)
            {
                try
                {
                    await provider.CreateFolderAsync(current, ct);
                }
                catch (StorageException ex) when (ex.Code == StorageError.Conflict)
                {
                    // made at the same moment by another request
                }
            }
            else if (!entry.IsFolder)
            {
                throw new StorageException(StorageError.Conflict);
            }
        }
    }
}

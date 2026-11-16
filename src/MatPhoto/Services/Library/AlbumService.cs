using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>A card of the album list.</summary>
public sealed record AlbumCard(long Id, string Name, string Title, DateOnly? AlbumDate, DateTime? StartDate, DateTime? EndDate, int MediaCount, long Bytes, long StorageId, string StorageName,
    string Path, long? CoverId, string? CoverHash);

public sealed record AlbumCriteria(string? Search, long? StorageId, int? Year, string Sort, bool Descending, int Page, int PageSize);

public sealed record AlbumList(IReadOnlyList<AlbumCard> Items, int Total, int Matching, IReadOnlyList<(long Id, string Name)> Storages, IReadOnlyList<int> Years);

/// <summary>An album for its page and its edit page.</summary>
public sealed record AlbumDetail(long Id, string Name, string Title, DateOnly? AlbumDate, string? Description, string Path, long StorageId, string StorageName, int MediaCount, long Bytes,
    DateTime? StartDate, DateTime? EndDate, long? CoverId, bool CanEdit, bool StorageReadOnly);

/// <summary>The form of an album (new or changed).</summary>
public sealed record AlbumInput(long StorageId, string? ParentFolder, DateOnly? Date, string? Title, string? Description, long? CoverId);

/// <summary>
/// The albums: the folders of the library storages. Creating an album makes its folder, renaming it renames the folder on the storage (and the paths of everything in it),
/// deleting it moves the folder into the trash folder of the storage (<see cref="MediaFormats.TrashFolder"/>) – nothing is ever deleted for good by MatPhoto. Seeing
/// needs the role Viewer on the storage, every change Editor.
/// </summary>
public static class AlbumService
{
    public static async Task<AlbumList> ListAsync(AppDbContext db, long userId, AlbumCriteria criteria, CancellationToken ct = default)
    {
        var grants = (await StorageAccess.ListForPersonAsync(db, userId, ct)).Where(g => g.IsLibrary).ToList();
        var visible = grants.Select(g => g.Id).ToArray();
        var all = db.Albums.AsNoTracking().Where(a => visible.Contains(a.StorageId) && a.MediaCount > 0);
        var total = await all.CountAsync(ct);
        var query = all;
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var like = "%" + criteria.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(a => EF.Functions.ILike(a.Name, like) || EF.Functions.ILike(a.Path, like) || EF.Functions.ILike(a.Description ?? string.Empty, like));
        }

        if (criteria.StorageId is { } storage)
        {
            query = query.Where(a => a.StorageId == storage);
        }

        if (criteria.Year is { } year)
        {
            query = query.Where(a => a.StartDate != null && a.StartDate.Value.Year == year);
        }

        query = (criteria.Sort, criteria.Descending) switch
        {
            ("name", false) => query.OrderBy(a => a.Name),
            ("name", true) => query.OrderByDescending(a => a.Name),
            ("count", false) => query.OrderBy(a => a.MediaCount).ThenBy(a => a.Name),
            ("count", true) => query.OrderByDescending(a => a.MediaCount).ThenBy(a => a.Name),
            ("date", false) => query.OrderBy(a => a.StartDate).ThenBy(a => a.Name),
            _ => query.OrderByDescending(a => a.StartDate).ThenByDescending(a => a.Name),
        };

        var matching = await query.CountAsync(ct);
        var page = await query.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize)
            .Select(a => new
            {
                Album = a,
                Cover = a.CoverMediaId ?? db.Media.Where(m => m.AlbumId == a.Id && m.ThumbState == ThumbState.Ready && (m.Traits & MediaTraits.LiveVideo) == 0)
                    .OrderBy(m => m.SortDate).Select(m => (long?)m.Id).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var coverIds = page.Where(p => p.Cover != null).Select(p => p.Cover!.Value).ToList();
        var hashes = await db.Media.Where(m => coverIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.ContentHash, ct);
        var names = grants.ToDictionary(g => g.Id, g => g.Name);
        var years = await all.Where(a => a.StartDate != null).Select(a => a.StartDate!.Value.Year).Distinct().OrderByDescending(y => y).ToListAsync(ct);
        return new AlbumList(
            page.Select(p => new AlbumCard(p.Album.Id, p.Album.Name, p.Album.Title, p.Album.AlbumDate, p.Album.StartDate, p.Album.EndDate, p.Album.MediaCount, p.Album.Bytes, p.Album.StorageId,
                names.GetValueOrDefault(p.Album.StorageId, string.Empty), p.Album.Path, p.Cover, p.Cover is { } c ? hashes.GetValueOrDefault(c) : null)).ToList(),
            total, matching, grants.Select(g => (g.Id, g.Name)).ToList(), years);
    }

    public static async Task<AlbumDetail?> GetAsync(AppDbContext db, long userId, long id, CancellationToken ct = default)
    {
        var album = await db.Albums.AsNoTracking().Include(a => a.Storage).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album?.Storage is null)
        {
            return null;
        }

        var role = await StorageAccess.RoleAsync(db, userId, album.Storage, ct);
        if (role == AccessRole.None || !album.Storage.IsLibrary)
        {
            return null;
        }

        return new AlbumDetail(album.Id, album.Name, album.Title, album.AlbumDate, album.Description, album.Path, album.StorageId, album.Storage.Name, album.MediaCount, album.Bytes,
            album.StartDate, album.EndDate, album.CoverMediaId, role >= AccessRole.Editor, album.Storage.IsReadOnly);
    }

    /// <summary>The storages a person may make albums in (Editor of a library storage that is not read-only).</summary>
    public static async Task<IReadOnlyList<StorageGrant>> TargetsAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => (await StorageAccess.ListForPersonAsync(db, userId, ct)).Where(g => g.IsLibrary && g.Role >= AccessRole.Editor && !g.IsReadOnly).ToList();

    /// <summary>Makes a new album: the folder <c>&lt;parent&gt;/&lt;date title&gt;</c> on the storage, and its record.</summary>
    public static async Task<(long? Id, List<FieldError> Errors)> CreateAsync(AppDbContext db, StorageConnections connections, LibraryOptions options, long userId, AlbumInput input, CancellationToken ct = default)
    {
        var found = await StorageAccess.FindAsync(db, userId, input.StorageId, AccessRole.Editor, ct);
        if (found is not { Storage.IsLibrary: true } || found.Value.Storage.IsReadOnly)
        {
            return (null, [new FieldError(nameof(AlbumInput.StorageId), Text.T("Albums.Error.Storage"))]);
        }

        var name = AlbumNames.Compose(input.Date, input.Title, options.AlbumDateFormat);
        var errors = Validate(name, input.Description);
        string parent;
        try
        {
            parent = PathGuard.Normalize(input.ParentFolder ?? string.Empty);
        }
        catch (StorageException)
        {
            errors.Add(new FieldError(nameof(AlbumInput.ParentFolder), Text.T("Albums.Error.Folder")));
            parent = string.Empty;
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var path = parent.Length == 0 ? name : $"{parent}/{name}";
        if (await db.Albums.AnyAsync(a => a.StorageId == input.StorageId && a.Path == path, ct))
        {
            return (null, [new FieldError(nameof(AlbumInput.Title), Text.T("Albums.Error.Exists"))]);
        }

        var provider = connections.Create(found.Value.Storage);
        try
        {
            if (await provider.GetAsync(path, ct) is null)
            {
                await provider.CreateFolderAsync(path, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, [new FieldError(nameof(AlbumInput.Title), StorageErrors.Message(ex))]);
        }

        var (date, title) = AlbumNames.Parse(name);
        var album = db.Albums.Add(new Album
        {
            StorageId = input.StorageId, Path = path, Name = name, Title = title, AlbumDate = date, Description = Clean(input.Description),
        }).Entity;
        ActivityLog.Add(db, userId, "album", name, "create", storageId: input.StorageId);
        await db.SaveChangesAsync(ct);
        return (album.Id, []);
    }

    /// <summary>Changes an album: a new name renames its folder (and moves the paths of everything inside), the description and the cover are only MatPhoto's.</summary>
    public static async Task<(bool Found, List<FieldError> Errors)> SaveAsync(AppDbContext db, StorageConnections connections, LibraryOptions options, long userId, long id, AlbumInput input,
        CancellationToken ct = default)
    {
        var album = await db.Albums.Include(a => a.Storage).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album?.Storage is null || await StorageAccess.RoleAsync(db, userId, album.Storage, ct) < AccessRole.Editor)
        {
            return (false, []);
        }

        var name = album.Path.Length == 0 ? album.Name : AlbumNames.Compose(input.Date, input.Title, options.AlbumDateFormat);
        var errors = Validate(name, input.Description);
        if (input.CoverId is { } cover && !await db.Media.AnyAsync(m => m.Id == cover && m.AlbumId == id, ct))
        {
            errors.Add(new FieldError(nameof(AlbumInput.CoverId), Text.T("Albums.Error.Cover")));
        }

        if (errors.Count > 0)
        {
            return (true, errors);
        }

        if (name != album.Name)
        {
            var parent = AlbumNames.ParentOf(album.Path);
            var path = parent.Length == 0 ? name : $"{parent}/{name}";
            if (await db.Albums.AnyAsync(a => a.StorageId == album.StorageId && a.Path == path && a.Id != id, ct))
            {
                return (true, [new FieldError(nameof(AlbumInput.Title), Text.T("Albums.Error.Exists"))]);
            }

            try
            {
                await connections.Create(album.Storage).MoveAsync(album.Path, path, overwrite: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (true, [new FieldError(nameof(AlbumInput.Title), StorageErrors.Message(ex))]);
            }

            await MovePathsAsync(db, album.StorageId, album.Path, path, ct);
            ActivityLog.Add(db, userId, "album", name, $"rename from {album.Name}", storageId: album.StorageId, albumId: id);
            album.Path = path;
            album.Name = name;
            (album.AlbumDate, album.Title) = AlbumNames.Parse(name);
        }

        album.Description = Clean(input.Description);
        album.CoverMediaId = input.CoverId;
        album.UpdateState = SoftEntity.Updated;
        await db.SaveChangesAsync(ct);
        return (true, []);
    }

    /// <summary>Moves the folder of the album into the trash folder of its storage and removes the album (and the albums below it) from the library.</summary>
    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, StorageConnections connections, long userId, long id, CancellationToken ct = default)
    {
        var album = await db.Albums.Include(a => a.Storage).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album?.Storage is null || await StorageAccess.RoleAsync(db, userId, album.Storage, ct) < AccessRole.Editor)
        {
            return Outcome<string>.Denied();
        }

        if (album.Path.Length == 0)
        {
            return Outcome<string>.Fail(Text.T("Albums.Error.Root"));
        }

        var target = $"{MediaFormats.TrashFolder}/{DateTime.UtcNow:yyyyMMdd-HHmmss} {AlbumNames.FolderName(album.Path)}";
        try
        {
            await connections.Create(album.Storage).MoveAsync(album.Path, target, overwrite: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Outcome<string>.Fail(StorageErrors.Message(ex));
        }

        var below = album.Path + "/";
        await db.Media.Where(m => m.StorageId == album.StorageId && (m.AlbumId == id || m.Path.StartsWith(below)))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.UpdateState, SoftEntity.Deleted).SetProperty(m => m.UpdateDate, DateTime.UtcNow), ct);
        await db.Albums.Where(a => a.StorageId == album.StorageId && (a.Id == id || a.Path.StartsWith(below)))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.UpdateState, SoftEntity.Deleted).SetProperty(a => a.UpdateDate, DateTime.UtcNow), ct);
        ActivityLog.Add(db, userId, "album", album.Name, "trash", storageId: album.StorageId, albumId: id);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(album.Name);
    }

    /// <summary>The paths of everything below a folder that moved (<c>old/…</c> becomes <c>new/…</c>): the albums and the photos.</summary>
    internal static async Task MovePathsAsync(AppDbContext db, long storageId, string from, string to, CancellationToken ct)
    {
        var below = from + "/";
        var cut = from.Length;
        await db.Media.Where(m => m.StorageId == storageId && m.Path.StartsWith(below))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Path, m => to + m.Path.Substring(cut)), ct);
        await db.Albums.Where(a => a.StorageId == storageId && a.Path.StartsWith(below))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Path, a => to + a.Path.Substring(cut)), ct);
    }

    private static List<FieldError> Validate(string name, string? description)
    {
        var errors = new List<FieldError>();
        if (name.Length == 0)
        {
            errors.Add(new FieldError(nameof(AlbumInput.Title), Text.T("Common.NameRequired")));
        }

        if ((description ?? string.Empty).Length > 2000)
        {
            errors.Add(new FieldError(nameof(AlbumInput.Description), Text.T("Albums.Error.TooLong")));
        }

        return errors;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

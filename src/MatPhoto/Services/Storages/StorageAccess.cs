using MatPhoto.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Storages;

/// <summary>A storage a person can open, with their role on it.</summary>
public sealed record StorageGrant(long Id, string Name, StorageKind Kind, AccessRole Role, bool IsLibrary, bool IsReadOnly);

/// <summary>
/// Who may see and change the photos of a storage: every person with the role "for everyone", or the people in its access list. Administrators manage storages but
/// see their photos only when they are allowed like anybody else. Every query of the library is limited to <see cref="VisibleIdsAsync"/> of the person, so a photo
/// of a storage that a person may not see does not exist for them (404).
/// </summary>
public static class StorageAccess
{
    /// <summary>The role of a person on a storage (None when they may not see it, or when it is switched off).</summary>
    public static async Task<AccessRole> RoleAsync(AppDbContext db, long userId, Storage storage, CancellationToken ct = default)
    {
        if (!storage.IsActive)
        {
            return AccessRole.None;
        }

        var role = storage.ForEveryone ? storage.EveryoneRole : AccessRole.None;
        var listed = await db.StorageAccess.AsNoTracking().Where(a => a.StorageId == storage.Id && a.UserId == userId).Select(a => (AccessRole?)a.Role).FirstOrDefaultAsync(ct);
        if (listed is { } r && r > role)
        {
            role = r;
        }

        return storage.IsReadOnly && role > AccessRole.Viewer ? AccessRole.Viewer : role;
    }

    /// <summary>The storage with the role of the person, or null when they may not see it (or not with <paramref name="minimum"/>).</summary>
    public static async Task<(Storage Storage, AccessRole Role)?> FindAsync(AppDbContext db, long userId, long storageId, AccessRole minimum = AccessRole.Viewer, CancellationToken ct = default)
    {
        var storage = await db.Storages.AsNoTracking().FirstOrDefaultAsync(l => l.Id == storageId, ct);
        if (storage is null)
        {
            return null;
        }

        var role = await RoleAsync(db, userId, storage, ct);
        return role >= minimum && role > AccessRole.None ? (storage, role) : null;
    }

    /// <summary>Every active storage the person can open, with their role (sorted by name).</summary>
    public static async Task<IReadOnlyList<StorageGrant>> ListForPersonAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var rows = await db.Storages.AsNoTracking()
            .Where(l => l.IsActive && (l.ForEveryone || l.Access.Any(a => a.UserId == userId)))
            .Select(l => new { l.Id, l.Name, l.Kind, l.ForEveryone, l.EveryoneRole, l.IsReadOnly, l.IsLibrary, Listed = l.Access.Where(a => a.UserId == userId).Select(a => (AccessRole?)a.Role).FirstOrDefault() })
            .ToListAsync(ct);
        return rows
            .Select(r =>
            {
                var role = r.ForEveryone ? r.EveryoneRole : AccessRole.None;
                if (r.Listed is { } listed && listed > role)
                {
                    role = listed;
                }

                return new StorageGrant(r.Id, r.Name, r.Kind, r.IsReadOnly && role > AccessRole.Viewer ? AccessRole.Viewer : role, r.IsLibrary, r.IsReadOnly);
            })
            .Where(r => r.Role > AccessRole.None)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The ids of the library storages whose photos the person may see: every query of photos and albums is limited to them.</summary>
    public static async Task<long[]> VisibleIdsAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => (await ListForPersonAsync(db, userId, ct)).Where(s => s.IsLibrary).Select(s => s.Id).ToArray();

    /// <summary>The ids of the library storages the person may change (import into, albums, trash).</summary>
    public static async Task<long[]> WritableIdsAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => (await ListForPersonAsync(db, userId, ct)).Where(s => s.IsLibrary && s.Role >= AccessRole.Editor).Select(s => s.Id).ToArray();
}

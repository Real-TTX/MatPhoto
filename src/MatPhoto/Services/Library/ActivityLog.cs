using MatPhoto.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

public sealed record ActivityRow(long Id, DateTime Date, string Action, string? Target, string? Detail, string? UserName, long? StorageId, long? AlbumId);

/// <summary>What happened: scans, imports, albums, the trash, storages, settings (the log of the administration). Entries are added with the change they describe.</summary>
public static class ActivityLog
{
    public static void Add(AppDbContext db, long? userId, string action, string? target = null, string? detail = null, long? storageId = null, long? albumId = null, long? mediaId = null)
    {
        db.Activities.Add(new Activity
        {
            UserId = userId,
            Action = action.Length > 30 ? action[..30] : action,
            Target = target is { Length: > 255 } t ? t[..255] : target,
            Detail = detail is { Length: > 1000 } d ? d[..1000] : detail,
            StorageId = storageId,
            AlbumId = albumId,
            MediaId = mediaId,
            OccurredDate = DateTime.UtcNow,
        });
    }

    /// <summary>The log for administrators (null for anybody else), newest first.</summary>
    public static async Task<(IReadOnlyList<ActivityRow> Rows, int Total)?> AllAsync(AppDbContext db, long adminId, string? search, string? action, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive, ct))
        {
            return null;
        }

        var query = db.Activities.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(a => EF.Functions.ILike(a.Target ?? string.Empty, pattern) || EF.Functions.ILike(a.Detail ?? string.Empty, pattern)
                || db.Users.IgnoreQueryFilters().Any(u => u.Id == a.UserId && EF.Functions.ILike(u.DisplayName, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .Select(a => new ActivityRow(a.Id, a.OccurredDate, a.Action, a.Target, a.Detail,
                db.Users.IgnoreQueryFilters().Where(u => u.Id == a.UserId).Select(u => u.DisplayName).FirstOrDefault(), a.StorageId, a.AlbumId))
            .ToListAsync(ct);
        return (rows, total);
    }
}

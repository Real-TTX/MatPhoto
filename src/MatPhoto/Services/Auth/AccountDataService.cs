using System.Text.Json;
using System.Text.Json.Serialization;
using MatPhoto.Data;
using MatPhoto.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Auth;

/// <summary>
/// The data of a person under the GDPR: the export of everything that is about them (Art. 15 and 20, a JSON file), and the erasure of the account (Art. 17).
/// <para>
/// The export holds the account and its settings, the favourites (the paths of the photos), the lists with their photos, the shared links (without their secret), the imports, the activity of the person and
/// the sessions. No secret: no password hash, no token. The photos themselves lie in the storages (the person downloads them there or in the viewer).
/// </para>
/// <para>
/// The erasure deletes for good: the account, its sessions, favourites, lists, shared links and imports (with their uploads). The photos a person imported stay in their albums:
/// they belong to the storage, not to the account.
/// </para>
/// </summary>
public sealed class AccountDataService(AppDbContext db, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The export as JSON (UTF-8).</summary>
    public async Task<byte[]> ExportAsync(long userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var export = new
        {
            Format = "MatPhoto export 1",
            ExportedAt = DateTime.UtcNow,
            Account = new
            {
                user.Email,
                user.DisplayName,
                user.Role,
                user.TimeZone,
                user.Language,
                user.Theme,
                user.Scheme,
                user.CreateDate,
                user.LastLoginDate,
                LinkedWithMatIdentity = user.ExternalSubject != null,
            },
            Favorites = await db.Favorites.AsNoTracking().Where(f => f.UserId == userId)
                .Select(f => new { Storage = f.Media!.Storage!.Name, f.Media.Path, Since = f.CreateDate }).ToListAsync(ct),
            Lists = await db.Collections.AsNoTracking().Where(c => c.OwnerId == userId)
                .Select(c => new
                {
                    c.Name, c.Description, c.Colour, c.IsShared, c.CreateDate,
                    Photos = c.Items.OrderBy(i => i.Position).Select(i => new { Storage = i.Media!.Storage!.Name, i.Media.Path }).ToList(),
                }).ToListAsync(ct),
            SharedLinks = await db.Shares.AsNoTracking().Where(s => s.OwnerId == userId)
                .Select(s => new { s.Title, s.Message, Album = s.Album!.Name, List = s.Collection!.Name, s.ExpiresDate, s.AllowDownload, s.ViewCount, s.LastViewedDate, s.CreateDate })
                .ToListAsync(ct),
            Imports = await db.ImportSessions.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.CreateDate)
                .Select(s => new { s.CreateDate, s.Source, s.State, s.TotalCount, s.ImportedCount, s.SkippedCount, s.FailedCount, s.FinishedDate }).ToListAsync(ct),
            Activity = await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredDate).Take(5000)
                .Select(a => new { a.OccurredDate, a.Action, a.Target, a.Detail }).ToListAsync(ct),
            Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new { s.CreateDate, s.LastSeenDate, s.ExpiresDate, s.IsRevoked, s.UserAgent, s.IpAddress }).ToListAsync(ct),
        };

        return JsonSerializer.SerializeToUtf8Bytes(export, Json);
    }

    /// <summary>Erases the account for good (see the class). The caller checks the password and that it is not the last administrator.</summary>
    public async Task EraseAsync(long userId, CancellationToken ct = default)
    {
        var imports = await db.ImportSessions.Where(s => s.UserId == userId).Select(s => s.Token).ToListAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Activities.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
            await db.Favorites.Where(f => f.UserId == userId).ExecuteDeleteAsync(ct);
            await db.CollectionItems.Where(i => i.Collection!.OwnerId == userId).ExecuteDeleteAsync(ct);
            await db.Shares.IgnoreQueryFilters().Where(s => s.OwnerId == userId).ExecuteDeleteAsync(ct);
            await db.Collections.IgnoreQueryFilters().Where(c => c.OwnerId == userId).ExecuteDeleteAsync(ct);
            await db.ImportSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
            await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        var data = configuration["DataDirectory"] ?? "/data";
        foreach (var token in imports)
        {
            ImportService.DeleteStaging(data, token);
        }
    }

    /// <summary>Is the person the last active administrator (who must not go, or nobody can run the installation)?</summary>
    public async Task<bool> IsLastAdminAsync(long userId)
        => await db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin && u.IsActive)
           && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && u.IsActive);
}

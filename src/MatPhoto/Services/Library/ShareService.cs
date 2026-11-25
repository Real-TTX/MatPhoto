using System.Buffers.Text;
using System.Security.Cryptography;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>What is shared: an album or a list, by its id and name.</summary>
public sealed record ShareTarget(bool IsAlbum, long Id, string Name)
{
    public MediaScope Scope => new(IsAlbum ? "album" : "list", Id);
}

public sealed record ShareRow(long Id, string Token, string Title, ShareTarget Target, DateOnly? ExpiresDate, bool Expired, bool AllowDownload, int Views, DateTime? LastViewed,
    DateTime CreateDate, bool IsMine, string OwnerName);

public sealed record ShareCriteria(string? Search, string? Show, string Sort, bool Descending, int Page, int PageSize);

public sealed record ShareList(IReadOnlyList<ShareRow> Items, int Total, int Matching);

public sealed record ShareDetail(long Id, string Token, string? Title, string? Message, DateOnly? ExpiresDate, bool Expired, bool AllowDownload, int Views, DateTime? LastViewed,
    ShareTarget Target, bool IsMine, string OwnerName);

public sealed record ShareInput(string? Title, string? Message, DateOnly? ExpiresDate, bool AllowDownload);

/// <summary>A link as a visitor opens it: whose eyes it looks through (<see cref="OwnerId"/>), what it shows, and what the visitor may do.</summary>
public sealed record SharedView(long ShareId, string Token, long OwnerId, MediaScope Scope, string Title, string? Message, string OwnerName, bool AllowDownload, DateOnly? ExpiresDate);

/// <summary>A file of a link: its record (with the storage) to read the file or the thumbnail.</summary>
public sealed record SharedFile(long Id, Storage Storage, string Path, string FileName, string MimeType, long Size, DateTime Modified, MediaType Type, string Hash);

/// <summary>
/// Links for people without an account (<c>/s/{token}</c>): one album or one list, seen through the eyes of the person who made the link – what they may see of it, as
/// long as they may see it, are active, the link has not expired and sharing is on. Every request of a visitor starts with <see cref="OpenAsync"/>; a photo outside
/// the album or the list does not exist for the link (404). A person shares what they may see; only they change their link, an administrator may also withdraw it.
/// </summary>
public static class ShareService
{
    public const int TitleMax = 200;

    public const int MessageMax = 2000;

    private const int TokenLength = 32;

    /// <summary>A new secret: 24 random bytes, 32 characters of base64url.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    /// <summary>The form of a token (anything else is not even looked up).</summary>
    public static bool IsToken(string? token)
        => token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The album or the list a person may share (it must be one they may see); null otherwise.</summary>
    public static async Task<ShareTarget?> TargetAsync(AppDbContext db, long userId, long? albumId, long? listId, CancellationToken ct = default)
    {
        if (albumId is { } a)
        {
            var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
            var name = await db.Albums.AsNoTracking().Where(x => x.Id == a && visible.Contains(x.StorageId)).Select(x => x.Name).FirstOrDefaultAsync(ct);
            return name is null ? null : new ShareTarget(true, a, name);
        }

        if (listId is { } l)
        {
            var name = await db.Collections.AsNoTracking().Where(c => c.Id == l && (c.OwnerId == userId || c.IsShared)).Select(c => c.Name).FirstOrDefaultAsync(ct);
            return name is null ? null : new ShareTarget(false, l, name);
        }

        return null;
    }

    /// <summary>The links of the person (an administrator may ask for everybody's: <c>show=all</c>).</summary>
    public static async Task<ShareList> ListAsync(AppDbContext db, long userId, bool isAdmin, ShareCriteria criteria, CancellationToken ct = default)
    {
        var all = isAdmin && criteria.Show == "all" ? db.Shares.AsNoTracking() : db.Shares.AsNoTracking().Where(s => s.OwnerId == userId);
        var total = await db.Shares.CountAsync(s => s.OwnerId == userId, ct);
        var rows = all.Select(s => new
        {
            s.Id, s.Token, s.Title, s.AlbumId, s.CollectionId, AlbumName = s.Album!.Name, ListName = s.Collection!.Name, s.ExpiresDate, s.AllowDownload, s.ViewCount,
            s.LastViewedDate, s.CreateDate, s.OwnerId, Owner = s.Owner!.DisplayName, Zone = s.Owner.TimeZone,
        });
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var like = "%" + criteria.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            rows = rows.Where(r => EF.Functions.ILike(r.Title ?? string.Empty, like) || EF.Functions.ILike(r.AlbumName ?? string.Empty, like)
                || EF.Functions.ILike(r.ListName ?? string.Empty, like));
        }

        rows = (criteria.Sort, criteria.Descending) switch
        {
            ("views", false) => rows.OrderBy(r => r.ViewCount).ThenByDescending(r => r.CreateDate),
            ("views", true) => rows.OrderByDescending(r => r.ViewCount).ThenByDescending(r => r.CreateDate),
            ("expires", false) => rows.OrderBy(r => r.ExpiresDate == null).ThenBy(r => r.ExpiresDate),
            ("expires", true) => rows.OrderBy(r => r.ExpiresDate == null).ThenByDescending(r => r.ExpiresDate),
            ("created", false) => rows.OrderBy(r => r.CreateDate),
            _ => rows.OrderByDescending(r => r.CreateDate),
        };

        var matching = await rows.CountAsync(ct);
        var page = await rows.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).ToListAsync(ct);
        return new ShareList(page.Select(p =>
        {
            var target = p.AlbumId is { } a ? new ShareTarget(true, a, p.AlbumName ?? string.Empty) : new ShareTarget(false, p.CollectionId ?? 0, p.ListName ?? string.Empty);
            return new ShareRow(p.Id, p.Token, p.Title ?? target.Name, target, p.ExpiresDate, IsExpired(p.ExpiresDate, p.Zone), p.AllowDownload, p.ViewCount, p.LastViewedDate,
                p.CreateDate, p.OwnerId == userId, p.Owner);
        }).ToList(), total, matching);
    }

    /// <summary>One link: the person's own, or anybody's for an administrator (who may only withdraw it). Null otherwise.</summary>
    public static async Task<ShareDetail?> GetAsync(AppDbContext db, long userId, bool isAdmin, long id, CancellationToken ct = default)
    {
        var s = await db.Shares.AsNoTracking().Where(x => x.Id == id && (x.OwnerId == userId || isAdmin))
            .Select(x => new
            {
                x.Id, x.Token, x.Title, x.Message, x.ExpiresDate, x.AllowDownload, x.ViewCount, x.LastViewedDate, x.AlbumId, x.CollectionId, AlbumName = x.Album!.Name,
                ListName = x.Collection!.Name, x.OwnerId, Owner = x.Owner!.DisplayName, Zone = x.Owner.TimeZone,
            })
            .FirstOrDefaultAsync(ct);
        if (s is null)
        {
            return null;
        }

        var target = s.AlbumId is { } a ? new ShareTarget(true, a, s.AlbumName ?? string.Empty) : new ShareTarget(false, s.CollectionId ?? 0, s.ListName ?? string.Empty);
        return new ShareDetail(s.Id, s.Token, s.Title, s.Message, s.ExpiresDate, IsExpired(s.ExpiresDate, s.Zone), s.AllowDownload, s.ViewCount, s.LastViewedDate, target,
            s.OwnerId == userId, s.Owner);
    }

    /// <summary>Makes a link to an album or a list the person may see. Answers the id of the link, or the errors of the form.</summary>
    public static async Task<(long? Id, List<FieldError> Errors)> CreateAsync(AppDbContext db, long userId, ShareTarget target, ShareInput input, DateOnly today,
        CancellationToken ct = default)
    {
        if (await TargetAsync(db, userId, target.IsAlbum ? target.Id : null, target.IsAlbum ? null : target.Id, ct) is not { } allowed)
        {
            return (null, []);
        }

        var errors = Check(input, today, null);
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var share = db.Shares.Add(new Share
        {
            Token = NewToken(),
            OwnerId = userId,
            AlbumId = allowed.IsAlbum ? allowed.Id : null,
            CollectionId = allowed.IsAlbum ? null : allowed.Id,
        }).Entity;
        Apply(share, input);
        ActivityLog.Add(db, userId, "share", share.Title ?? allowed.Name, Text.T("Shares.Activity.Created"), albumId: share.AlbumId);
        await db.SaveChangesAsync(ct);
        return (share.Id, []);
    }

    /// <summary>Changes a link of the person (title, words, end, downloads). Null id when it is not theirs.</summary>
    public static async Task<(long? Id, List<FieldError> Errors)> SaveAsync(AppDbContext db, long userId, long id, ShareInput input, DateOnly today, CancellationToken ct = default)
    {
        var share = await db.Shares.FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == userId, ct);
        if (share is null)
        {
            return (null, []);
        }

        var errors = Check(input, today, share.ExpiresDate);
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        Apply(share, input);
        share.UpdateState = SoftEntity.Updated;
        await db.SaveChangesAsync(ct);
        return (share.Id, []);
    }

    /// <summary>Withdraws a link (the person's own; an administrator any): the address stops working at once. Answers its token, null when there is none.</summary>
    public static async Task<string?> DeleteAsync(AppDbContext db, long userId, bool isAdmin, long id, CancellationToken ct = default)
    {
        var share = await db.Shares.Include(s => s.Album).Include(s => s.Collection).FirstOrDefaultAsync(s => s.Id == id && (s.OwnerId == userId || isAdmin), ct);
        if (share is null)
        {
            return null;
        }

        db.Shares.Remove(share);
        ActivityLog.Add(db, userId, "share", share.Title ?? share.Album?.Name ?? share.Collection?.Name, Text.T("Shares.Activity.Withdrawn"), albumId: share.AlbumId);
        await db.SaveChangesAsync(ct);
        return share.Token;
    }

    /// <summary>The links that show an album or a list (the button "Share" of its page says how many there are).</summary>
    public static Task<int> CountForAsync(AppDbContext db, long userId, ShareTarget target, CancellationToken ct = default)
        => db.Shares.CountAsync(s => s.OwnerId == userId && (target.IsAlbum ? s.AlbumId == target.Id : s.CollectionId == target.Id), ct);

    // ---- the visitors ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a link for a visitor: null when the token is unknown or withdrawn, the link has expired, sharing is off, the person who made it is gone or switched off,
    /// or they may not see the album or the list any more.
    /// </summary>
    public static async Task<SharedView?> OpenAsync(AppDbContext db, string? token, bool sharingEnabled, CancellationToken ct = default)
    {
        if (!sharingEnabled || !IsToken(token))
        {
            return null;
        }

        var s = await db.Shares.AsNoTracking().Where(x => x.Token == token)
            .Select(x => new { x.Id, x.OwnerId, x.AlbumId, x.CollectionId, x.Title, x.Message, x.AllowDownload, x.ExpiresDate })
            .FirstOrDefaultAsync(ct);
        if (s is null)
        {
            return null;
        }

        var owner = await db.Users.AsNoTracking().Where(u => u.Id == s.OwnerId && u.IsActive).Select(u => new { u.DisplayName, u.TimeZone }).FirstOrDefaultAsync(ct);
        if (owner is null || IsExpired(s.ExpiresDate, owner.TimeZone))
        {
            return null;
        }

        var target = await TargetAsync(db, s.OwnerId, s.AlbumId, s.AlbumId is null ? s.CollectionId : null, ct);
        return target is null ? null : new SharedView(s.Id, token!, s.OwnerId, target.Scope, s.Title ?? target.Name, s.Message, owner.DisplayName, s.AllowDownload, s.ExpiresDate);
    }

    /// <summary>Counts a visit (the page, not every picture of it).</summary>
    public static Task CountVisitAsync(AppDbContext db, long shareId, CancellationToken ct = default)
        => db.Shares.Where(s => s.Id == shareId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ViewCount, s => s.ViewCount + 1).SetProperty(s => s.LastViewedDate, DateTime.UtcNow), ct);

    /// <summary>A photo of the link (or the video of a Live Photo of it); null when it is not part of what the link shows.</summary>
    public static async Task<SharedFile?> FileAsync(AppDbContext db, SharedView view, long mediaId, CancellationToken ct = default)
    {
        var query = await LibraryService.QueryAsync(db, view.OwnerId, view.Scope, ct);
        if (query is null)
        {
            return null;
        }

        var shown = await query.AnyAsync(m => m.Id == mediaId, ct);
        if (!shown && !await query.AnyAsync(m => m.LiveVideoId == mediaId, ct))
        {
            return null;
        }

        var m = await db.Media.AsNoTracking().Include(x => x.Storage).FirstOrDefaultAsync(x => x.Id == mediaId, ct);
        return m?.Storage is null ? null : new SharedFile(m.Id, m.Storage, m.Path, m.FileName, m.MimeType, m.Size, m.FileModified, m.Type, m.ContentHash);
    }

    /// <summary>Every file of the link for the ZIP (with the videos of the Live Photos), in the order of the grid.</summary>
    public static async Task<IReadOnlyList<SharedFile>> FilesAsync(AppDbContext db, SharedView view, CancellationToken ct = default)
    {
        var query = await LibraryService.QueryAsync(db, view.OwnerId, view.Scope, ct);
        if (query is null)
        {
            return [];
        }

        var ids = await query.OrderBy(m => m.SortDate).ThenBy(m => m.Id).Select(m => new { m.Id, m.LiveVideoId }).ToListAsync(ct);
        var all = ids.SelectMany(x => x.LiveVideoId is { } live ? new[] { x.Id, live } : [x.Id]).ToList();
        var rows = await db.Media.AsNoTracking().Include(m => m.Storage).Where(m => all.Contains(m.Id)).ToListAsync(ct);
        var order = all.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return rows.Where(m => m.Storage is not null).OrderBy(m => order[m.Id])
            .Select(m => new SharedFile(m.Id, m.Storage!, m.Path, m.FileName, m.MimeType, m.Size, m.FileModified, m.Type, m.ContentHash)).ToList();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    private static bool IsExpired(DateOnly? expires, string? zone) => expires is { } last && last < Dates.Today(zone);

    private static List<FieldError> Check(ShareInput input, DateOnly today, DateOnly? before)
    {
        var errors = new List<FieldError>();
        if ((input.Title ?? string.Empty).Trim().Length > TitleMax)
        {
            errors.Add(new FieldError(nameof(ShareInput.Title), Text.T("Shares.Error.TitleTooLong", TitleMax)));
        }

        if ((input.Message ?? string.Empty).Length > MessageMax)
        {
            errors.Add(new FieldError(nameof(ShareInput.Message), Text.T("Lists.Error.TooLong")));
        }

        // a date in the past only when it is the one the link has already (an expired link stays as it is until it gets a new date)
        if (input.ExpiresDate is { } last && last < today && last != before)
        {
            errors.Add(new FieldError(nameof(ShareInput.ExpiresDate), Text.T("Shares.Error.Past")));
        }

        return errors;
    }

    private static void Apply(Share share, ShareInput input)
    {
        share.Title = string.IsNullOrWhiteSpace(input.Title) ? null : input.Title.Trim();
        share.Message = string.IsNullOrWhiteSpace(input.Message) ? null : input.Message.Trim();
        share.ExpiresDate = input.ExpiresDate;
        share.AllowDownload = input.AllowDownload;
    }
}

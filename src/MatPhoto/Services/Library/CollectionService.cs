using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

public sealed record CollectionRow(long Id, string Name, string? Description, string? Colour, bool IsShared, bool IsMine, string OwnerName, int Count, DateTime UpdateDate, long? CoverId, string? CoverHash);

public sealed record CollectionCriteria(string? Search, string? Show, string Sort, bool Descending, int Page, int PageSize);

public sealed record CollectionList(IReadOnlyList<CollectionRow> Items, int Total, int Matching);

public sealed record CollectionDetail(long Id, string Name, string? Description, string? Colour, bool IsShared, bool IsMine, string OwnerName, int Count, long? CoverId);

public sealed record CollectionInput(string? Name, string? Description, string? Colour, bool IsShared, long? CoverId);

/// <summary>
/// The lists ("linked" albums): photos from any album put together, e.g. for a project. A list belongs to the person who made it; a shared list is seen by everybody
/// (each sees only the photos of the storages they may see) and everybody may add to it and take from it. Only the owner changes its name or deletes it.
/// </summary>
public static class CollectionService
{
    public const int NameMax = 120;

    public static Task<bool> CanSeeAsync(AppDbContext db, long userId, long id, CancellationToken ct = default)
        => db.Collections.AnyAsync(c => c.Id == id && (c.OwnerId == userId || c.IsShared), ct);

    public static async Task<CollectionList> ListAsync(AppDbContext db, long userId, CollectionCriteria criteria, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var all = db.Collections.AsNoTracking().Where(c => c.OwnerId == userId || c.IsShared);
        var total = await all.CountAsync(ct);
        var query = all;
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var like = "%" + criteria.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.Description ?? string.Empty, like));
        }

        query = criteria.Show switch
        {
            "mine" => query.Where(c => c.OwnerId == userId),
            "shared" => query.Where(c => c.IsShared),
            _ => query,
        };

        var rows = query.Select(c => new
        {
            c.Id, c.Name, c.Description, c.Colour, c.IsShared, c.OwnerId, Owner = c.Owner!.DisplayName, c.UpdateDate,
            Count = c.Items.Count(i => visible.Contains(i.Media!.StorageId) && i.Media.UpdateState != SoftEntity.Deleted),
            Cover = c.CoverMediaId ?? c.Items.Where(i => visible.Contains(i.Media!.StorageId) && i.Media.UpdateState != SoftEntity.Deleted && i.Media.ThumbState == ThumbState.Ready)
                .OrderBy(i => i.Position).Select(i => (long?)i.MediaId).FirstOrDefault(),
        });
        rows = (criteria.Sort, criteria.Descending) switch
        {
            ("count", false) => rows.OrderBy(r => r.Count).ThenBy(r => r.Name),
            ("count", true) => rows.OrderByDescending(r => r.Count).ThenBy(r => r.Name),
            ("updated", false) => rows.OrderBy(r => r.UpdateDate),
            ("updated", true) => rows.OrderByDescending(r => r.UpdateDate),
            (_, true) => rows.OrderByDescending(r => r.Name),
            _ => rows.OrderBy(r => r.Name),
        };

        var matching = await rows.CountAsync(ct);
        var page = await rows.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).ToListAsync(ct);
        var covers = page.Where(p => p.Cover != null).Select(p => p.Cover!.Value).ToList();
        var hashes = await db.Media.Where(m => covers.Contains(m.Id) && visible.Contains(m.StorageId)).ToDictionaryAsync(m => m.Id, m => m.ContentHash, ct);
        return new CollectionList(
            page.Select(p => new CollectionRow(p.Id, p.Name, p.Description, p.Colour, p.IsShared, p.OwnerId == userId, p.Owner, p.Count, p.UpdateDate, p.Cover,
                p.Cover is { } c ? hashes.GetValueOrDefault(c) : null)).ToList(),
            total, matching);
    }

    /// <summary>The lists a person can add photos to (theirs and the shared ones), for the dialog "Add to list".</summary>
    public static async Task<IReadOnlyList<(long Id, string Name, string? Colour)>> TargetsAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var rows = await db.Collections.AsNoTracking().Where(c => c.OwnerId == userId || c.IsShared).OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Colour }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name, r.Colour)).ToList();
    }

    public static async Task<CollectionDetail?> GetAsync(AppDbContext db, long userId, long id, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var c = await db.Collections.AsNoTracking().Where(x => x.Id == id && (x.OwnerId == userId || x.IsShared))
            .Select(x => new
            {
                x.Id, x.Name, x.Description, x.Colour, x.IsShared, x.OwnerId, Owner = x.Owner!.DisplayName, x.CoverMediaId,
                Count = x.Items.Count(i => visible.Contains(i.Media!.StorageId) && i.Media.UpdateState != SoftEntity.Deleted),
            })
            .FirstOrDefaultAsync(ct);
        return c is null ? null : new CollectionDetail(c.Id, c.Name, c.Description, c.Colour, c.IsShared, c.OwnerId == userId, c.Owner, c.Count, c.CoverMediaId);
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes a list of the person.</summary>
    public static async Task<(long? Id, List<FieldError> Errors)> SaveAsync(AppDbContext db, long userId, long? id, CollectionInput input, CancellationToken ct = default)
    {
        Collection? collection = null;
        if (id is { } existing)
        {
            collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == existing && c.OwnerId == userId, ct);
            if (collection is null)
            {
                return (null, []);
            }
        }

        var errors = new List<FieldError>();
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors.Add(new FieldError(nameof(CollectionInput.Name), Text.T("Common.NameRequired")));
        }
        else if (name.Length > NameMax)
        {
            errors.Add(new FieldError(nameof(CollectionInput.Name), Text.T("Lists.Error.NameTooLong", NameMax)));
        }

        if ((input.Description ?? string.Empty).Length > 2000)
        {
            errors.Add(new FieldError(nameof(CollectionInput.Description), Text.T("Lists.Error.TooLong")));
        }

        if (!string.IsNullOrEmpty(input.Colour) && !Ui.IsColour(input.Colour))
        {
            errors.Add(new FieldError(nameof(CollectionInput.Colour), Text.T("Lists.Error.Colour")));
        }

        if (input.CoverId is { } cover && collection is not null && !await db.CollectionItems.AnyAsync(i => i.CollectionId == collection.Id && i.MediaId == cover, ct))
        {
            errors.Add(new FieldError(nameof(CollectionInput.CoverId), Text.T("Lists.Error.Cover")));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        collection ??= db.Collections.Add(new Collection { OwnerId = userId }).Entity;
        collection.Name = name;
        collection.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        collection.Colour = string.IsNullOrEmpty(input.Colour) ? null : input.Colour.ToLowerInvariant();
        collection.IsShared = input.IsShared;
        collection.CoverMediaId = id is null ? null : input.CoverId;
        if (id is not null)
        {
            collection.UpdateState = SoftEntity.Updated;
        }

        await db.SaveChangesAsync(ct);
        return (collection.Id, []);
    }

    /// <summary>Makes a list with only a name (the dialog "Add to list" → "New list").</summary>
    public static async Task<Outcome<(long Id, string Name)>> QuickCreateAsync(AppDbContext db, long userId, string? name, CancellationToken ct = default)
    {
        var (id, errors) = await SaveAsync(db, userId, null, new CollectionInput(name, null, null, false, null), ct);
        return id is { } created ? Outcome<(long, string)>.Success((created, name!.Trim())) : Outcome<(long, string)>.Fail(errors.FirstOrDefault()?.Message ?? Text.T("Common.CheckForm"));
    }

    /// <summary>Deletes a list of the person (the photos stay where they are).</summary>
    public static async Task<string?> DeleteAsync(AppDbContext db, long userId, long id, CancellationToken ct = default)
    {
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == userId, ct);
        if (collection is null)
        {
            return null;
        }

        db.Collections.Remove(collection);
        await db.CollectionItems.Where(i => i.CollectionId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return collection.Name;
    }

    /// <summary>Puts photos (that the person may see) at the end of a list; those on it already stay where they are. Answers how many were added, null when the list is not theirs.</summary>
    public static async Task<int?> AddAsync(AppDbContext db, long userId, long id, IReadOnlyCollection<long> mediaIds, CancellationToken ct = default)
    {
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == id && (c.OwnerId == userId || c.IsShared), ct);
        if (collection is null)
        {
            return null;
        }

        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var allowed = await db.Media.Where(m => mediaIds.Contains(m.Id) && visible.Contains(m.StorageId)).Select(m => m.Id).ToListAsync(ct);
        var already = await db.CollectionItems.IgnoreQueryFilters().Where(i => i.CollectionId == id && allowed.Contains(i.MediaId)).Select(i => i.MediaId).ToListAsync(ct);
        var position = await db.CollectionItems.IgnoreQueryFilters().Where(i => i.CollectionId == id).MaxAsync(i => (int?)i.Position, ct) ?? 0;
        var order = mediaIds.Where(allowed.Contains).Except(already).Distinct().ToList();
        foreach (var mediaId in order)
        {
            db.CollectionItems.Add(new CollectionItem { CollectionId = id, MediaId = mediaId, Position = ++position });
        }

        collection.UpdateState = SoftEntity.Updated;
        await db.SaveChangesAsync(ct);
        return order.Count;
    }

    /// <summary>Takes photos off a list (they stay in their albums). Null when the list is not theirs.</summary>
    public static async Task<int?> RemoveAsync(AppDbContext db, long userId, long id, IReadOnlyCollection<long> mediaIds, CancellationToken ct = default)
    {
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == id && (c.OwnerId == userId || c.IsShared), ct);
        if (collection is null)
        {
            return null;
        }

        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var removed = await db.CollectionItems.Where(i => i.CollectionId == id && mediaIds.Contains(i.MediaId) && visible.Contains(i.Media!.StorageId)).ExecuteDeleteAsync(ct);
        if (collection.CoverMediaId is { } cover && mediaIds.Contains(cover))
        {
            collection.CoverMediaId = null;
        }

        collection.UpdateState = SoftEntity.Updated;
        await db.SaveChangesAsync(ct);
        return removed;
    }
}

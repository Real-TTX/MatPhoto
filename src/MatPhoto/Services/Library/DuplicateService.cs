using MatPhoto.Data;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>One photo of a group of duplicates, with what decides which one to keep.</summary>
public sealed record DuplicateItem(long Id, string FileName, string Path, string AlbumName, long AlbumId, string StorageName, long Size, int Width, int Height, DateTime SortDate,
    DateSource DateSource, string Hash, bool HasThumb, bool CanEdit, bool Keep);

/// <summary>Photos that are the same file (<see cref="Exact"/>) or look the same (another size, another compression, a burst).</summary>
public sealed record DuplicateGroup(string Key, bool Exact, IReadOnlyList<DuplicateItem> Items)
{
    public long Wasted => Exact ? Items.Where(i => !i.Keep).Sum(i => i.Size) : 0;
}

public sealed record DuplicateReport(IReadOnlyList<DuplicateGroup> Groups, int ExactGroups, int SimilarGroups, long Wasted);

/// <summary>
/// Finds duplicates among the photos a person may see: the same content (the SHA-256 of the file) and similar pictures (the 64-bit picture hashes differ in at most
/// <see cref="LibraryOptions.SimilarThreshold"/> bits). Similar candidates are found without comparing every pair: by four 16-bit parts of the hash (two copies of a
/// picture share at least one part), and among the photos of the same day (bursts). The photo suggested to keep is the largest picture, then the one with the date of
/// the camera, then the shortest path. Pairs that a person dismissed are not offered again.
/// </summary>
public static class DuplicateService
{
    private const int MaxGroups = 200;
    private const int MaxPerDay = 2500;

    public static async Task<DuplicateReport> FindAsync(AppDbContext db, long userId, int threshold, string kind, CancellationToken ct = default)
    {
        var grants = (await StorageAccess.ListForPersonAsync(db, userId, ct)).Where(g => g.IsLibrary).ToList();
        var visible = grants.Select(g => g.Id).ToArray();
        var editable = grants.Where(g => g.Role >= AccessRole.Editor && !g.IsReadOnly).Select(g => g.Id).ToHashSet();
        var rows = await LibraryService.Shown(db, visible)
            .Select(m => new Row(m.Id, m.ContentHash, m.PerceptualHash, m.Size, m.SortDate, m.Type))
            .ToListAsync(ct);
        var dismissed = (await db.DuplicateDismissals.Select(d => new { d.FirstMediaId, d.SecondMediaId }).ToListAsync(ct))
            .Select(d => (d.FirstMediaId, d.SecondMediaId)).ToHashSet();

        var exact = rows.Where(r => r.Hash.Length > 0).GroupBy(r => (r.Hash, r.Size)).Where(g => g.Count() > 1)
            .Select(g => g.Select(r => r.Id).ToList())
            .Select(ids => ids.Where(a => ids.Any(b => a != b && !dismissed.Contains(Pair(a, b)))).ToList())
            .Where(ids => ids.Count > 1)
            .ToList();
        var similar = threshold > 0 ? Similar(rows.Where(r => r.Type == MediaType.Photo && r.PHash is not null).ToList(), threshold, dismissed) : [];

        var chosen = new List<(List<long> Ids, bool Exact)>();
        if (kind != "similar")
        {
            chosen.AddRange(exact.Select(ids => (ids, true)));
        }

        if (kind != "exact")
        {
            chosen.AddRange(similar.Select(ids => (ids, false)));
        }

        var page = chosen.Take(MaxGroups).ToList();
        var ids = page.SelectMany(g => g.Ids).Distinct().ToList();
        var details = await db.Media.AsNoTracking().Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.FileName, m.Path, Album = m.Album!.Name, m.AlbumId, Storage = m.Storage!.Name, m.StorageId, m.Size, m.Width, m.Height, m.SortDate, m.DateSource, m.ContentHash, m.ThumbState })
            .ToDictionaryAsync(m => m.Id, ct);

        var groups = new List<DuplicateGroup>();
        foreach (var (groupIds, isExact) in page)
        {
            var items = groupIds.Where(details.ContainsKey).Select(id => details[id]).ToList();
            if (items.Count < 2)
            {
                continue;
            }

            var keep = items.OrderByDescending(m => (long)m.Width * m.Height).ThenBy(m => m.DateSource == DateSource.Exif ? 0 : 1).ThenByDescending(m => m.Size).ThenBy(m => m.Path.Length).First().Id;
            groups.Add(new DuplicateGroup(
                string.Join('-', items.Select(i => i.Id).Order()),
                isExact,
                items.OrderByDescending(i => i.Id == keep).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new DuplicateItem(m.Id, m.FileName, m.Path, m.Album, m.AlbumId, m.Storage, m.Size, m.Width, m.Height, m.SortDate, m.DateSource, m.ContentHash,
                        m.ThumbState == ThumbState.Ready, editable.Contains(m.StorageId), m.Id == keep))
                    .ToList()));
        }

        return new DuplicateReport(groups, exact.Count, similar.Count, groups.Sum(g => g.Wasted));
    }

    /// <summary>Remembers that the photos of a group are not duplicates of each other (only photos the person may see).</summary>
    public static async Task<int> DismissAsync(AppDbContext db, long userId, IReadOnlyCollection<long> mediaIds, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var ids = await db.Media.Where(m => mediaIds.Contains(m.Id) && visible.Contains(m.StorageId)).Select(m => m.Id).OrderBy(id => id).ToListAsync(ct);
        var known = (await db.DuplicateDismissals.Where(d => ids.Contains(d.FirstMediaId) && ids.Contains(d.SecondMediaId)).Select(d => new { d.FirstMediaId, d.SecondMediaId }).ToListAsync(ct))
            .Select(d => (d.FirstMediaId, d.SecondMediaId)).ToHashSet();
        var added = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                if (known.Add((ids[i], ids[j])))
                {
                    db.DuplicateDismissals.Add(new DuplicateDismissal { FirstMediaId = ids[i], SecondMediaId = ids[j] });
                    added++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    private sealed record Row(long Id, string Hash, long? PHash, long Size, DateTime Date, MediaType Type);

    private static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);

    /// <summary>The groups of similar photos (pairs within the threshold, joined), leaving out groups that are only copies of one file.</summary>
    private static List<List<long>> Similar(List<Row> rows, int threshold, HashSet<(long, long)> dismissed)
    {
        var parent = rows.ToDictionary(r => r.Id, r => r.Id);
        long Find(long x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Compare(Row a, Row b)
        {
            if (a.Id == b.Id || (a.Hash == b.Hash && a.Size == b.Size) || dismissed.Contains(Pair(a.Id, b.Id)))
            {
                return;
            }

            if (ImageRenderer.Distance(a.PHash!.Value, b.PHash!.Value) <= threshold)
            {
                var (ra, rb) = (Find(a.Id), Find(b.Id));
                if (ra != rb)
                {
                    parent[ra] = rb;
                }
            }
        }

        // copies anywhere in the library: they share at least one 16-bit part of the hash
        for (var band = 0; band < 4; band++)
        {
            var shift = band * 16;
            foreach (var bucket in rows.GroupBy(r => (int)((ulong)r.PHash!.Value >> shift & 0xFFFF)).Where(g => g.Count() > 1))
            {
                var list = bucket.ToList();
                if (list.Count > 400)
                {
                    continue; // a part that very many pictures share says nothing (dark or plain pictures)
                }

                for (var i = 0; i < list.Count; i++)
                {
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        Compare(list[i], list[j]);
                    }
                }
            }
        }

        // bursts and retakes: the photos of the same day
        foreach (var day in rows.GroupBy(r => r.Date.Date).Where(g => g.Count() > 1))
        {
            var list = day.OrderBy(r => r.Date).Take(MaxPerDay).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count && (list[j].Date - list[i].Date).TotalMinutes <= 30; j++)
                {
                    Compare(list[i], list[j]);
                }
            }
        }

        var hashes = rows.ToDictionary(r => r.Id, r => r.Hash);
        return rows.GroupBy(r => Find(r.Id)).Where(g => g.Count() > 1)
            .Select(g => g.Select(r => r.Id).ToList())
            .Where(g => g.Select(id => hashes[id]).Distinct().Count() > 1)
            .OrderByDescending(g => g.Max())
            .ToList();
    }
}

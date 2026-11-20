using System.Globalization;
using MatPhoto.Data;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Library;

/// <summary>One month of a grid (or the whole of a list): the key the browser asks for, and how many photos it holds.</summary>
public sealed record TimelineBucket(string Key, int Year, int Month, int Count);

/// <summary>A tile of the grid: what the browser needs to lay it out and to show it.</summary>
public sealed record GridItem(long Id, int Width, int Height, string Hash, MediaType Type, int? DurationMs, bool Live, bool Favorite, DateTime Date, bool HasThumb);

/// <summary>Everything the info panel of the viewer shows about one photo.</summary>
public sealed record MediaDetail(
    long Id,
    string FileName,
    string Path,
    MediaType Type,
    MediaTraits Traits,
    string MimeType,
    long Size,
    int Width,
    int Height,
    DateTime? TakenDate,
    DateSource DateSource,
    DateTime SortDate,
    string? CameraMake,
    string? CameraModel,
    string? LensModel,
    double? FNumber,
    double? ExposureTime,
    int? Iso,
    double? FocalLength,
    double? Latitude,
    double? Longitude,
    double? Altitude,
    string? City,
    string? Region,
    string? Country,
    string? CountryCode,
    int? DurationMs,
    string? VideoCodec,
    long? LiveVideoId,
    string Hash,
    bool HasThumb,
    long AlbumId,
    string AlbumName,
    long StorageId,
    string StorageName,
    bool CanEdit,
    bool Favorite,
    IReadOnlyList<(long Id, string Name)> Lists,
    bool BrowserCanShow,
    IReadOnlyList<Faces.PhotoPerson> People);

/// <summary>A card of Explore: an automatic album with its number of photos and the photo on its cover.</summary>
public sealed record ExploreCard(string Scope, string Title, string? Subtitle, int Count, long? CoverId, string? CoverHash, string Icon);

public sealed record ExploreModel(IReadOnlyList<ExploreCard> Places, IReadOnlyList<ExploreCard> Countries, IReadOnlyList<ExploreCard> Types, IReadOnlyList<ExploreCard> Cameras, IReadOnlyList<ExploreCard> Years);

/// <summary>
/// The library as people see it: the grids (timeline, album, list, favourites, the automatic albums, the search) in months for endless scrolling, the details of one
/// photo, favourites, Explore. Every function first takes the storages the person may see (<see cref="StorageAccess.VisibleIdsAsync"/>); a photo outside of them, or of a
/// list that is not theirs, does not exist for them (null = 404).
/// </summary>
public static class LibraryService
{
    public const int MaxItemsPerCall = 1000;

    /// <summary>The photos of a scope the person may see (the video half of a Live Photo and the files not read yet left out); null when the scope is not theirs.</summary>
    public static async Task<IQueryable<Media>?> QueryAsync(AppDbContext db, long userId, MediaScope scope, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var query = Shown(db, visible);
        switch (scope.Kind)
        {
            case "album":
                if (!await db.Albums.AnyAsync(a => a.Id == scope.Id && visible.Contains(a.StorageId), ct))
                {
                    return null;
                }

                return query.Where(m => m.AlbumId == scope.Id);
            case "list":
                if (!await CollectionService.CanSeeAsync(db, userId, scope.Id ?? 0, ct))
                {
                    return null;
                }

                return query.Where(m => db.CollectionItems.Any(i => i.CollectionId == scope.Id && i.MediaId == m.Id));
            case "favorites":
                return query.Where(m => db.Favorites.Any(f => f.UserId == userId && f.MediaId == m.Id));
            case "person":
                // a person exists for the viewer only on the photos they may see
                var personId = scope.Id ?? 0;
                var photos = query.Where(m => db.Faces.Any(f => f.MediaId == m.Id && f.PersonId == personId && !f.IsIgnored));
                return await photos.AnyAsync(ct) ? photos : null;
            case "videos":
                return query.Where(m => m.Type == MediaType.Video);
            case "screenshots":
                return query.Where(m => (m.Traits & MediaTraits.Screenshot) != 0);
            case "selfies":
                return query.Where(m => (m.Traits & MediaTraits.Selfie) != 0);
            case "panoramas":
                return query.Where(m => (m.Traits & MediaTraits.Panorama) != 0);
            case "live":
                return query.Where(m => (m.Traits & MediaTraits.LivePhoto) != 0);
            case "animated":
                return query.Where(m => (m.Traits & MediaTraits.Animated) != 0);
            case "raw":
                return query.Where(m => (m.Traits & MediaTraits.Raw) != 0);
            case "nodate":
                return query.Where(m => m.DateSource == DateSource.FileDate);
            case "noplace":
                return query.Where(m => m.Latitude == null && m.Type == MediaType.Photo && (m.Traits & MediaTraits.Screenshot) == 0);
            case "place":
                var (country, city) = scope.Place;
                return city is null ? query.Where(m => m.CountryCode == country) : query.Where(m => m.CountryCode == country && m.City == city);
            case "camera":
                return query.Where(m => m.CameraModel == scope.Value);
            case "year":
                var year = (int)(scope.Id ?? 0);
                return query.Where(m => m.SortDate.Year == year);
            case "search":
                return Search(db, query, scope.Value ?? string.Empty);
            default:
                return query;
        }
    }

    /// <summary>The months of a grid with their numbers (a list: one bucket with all of it).</summary>
    public static async Task<IReadOnlyList<TimelineBucket>?> BucketsAsync(AppDbContext db, long userId, MediaScope scope, CancellationToken ct = default)
    {
        var query = await QueryAsync(db, userId, scope, ct);
        if (query is null)
        {
            return null;
        }

        if (scope.IsList)
        {
            var count = await query.CountAsync(ct);
            return count == 0 ? [] : [new TimelineBucket("all", 0, 0, count)];
        }

        var months = await query.GroupBy(m => new { m.SortDate.Year, m.SortDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync(ct);
        var ordered = scope.Ascending ? months.OrderBy(m => m.Year).ThenBy(m => m.Month) : months.OrderByDescending(m => m.Year).ThenByDescending(m => m.Month);
        return ordered.Select(m => new TimelineBucket($"{m.Year:D4}-{m.Month:D2}", m.Year, m.Month, m.Count)).ToList();
    }

    /// <summary>The tiles of one bucket (a month <c>2023-11</c>, or <c>all</c> of a list), <paramref name="limit"/> at a time.</summary>
    public static async Task<IReadOnlyList<GridItem>?> ItemsAsync(AppDbContext db, long userId, MediaScope scope, string bucket, int offset, int limit, CancellationToken ct = default)
    {
        var query = await QueryAsync(db, userId, scope, ct);
        if (query is null)
        {
            return null;
        }

        limit = Math.Clamp(limit, 1, MaxItemsPerCall);
        offset = Math.Max(0, offset);
        if (scope.IsList)
        {
            var listId = scope.Id ?? 0;
            query = query.OrderBy(m => db.CollectionItems.Where(i => i.CollectionId == listId && i.MediaId == m.Id).Select(i => i.Position).FirstOrDefault()).ThenBy(m => m.Id);
        }
        else
        {
            if (!TryMonth(bucket, out var from))
            {
                return [];
            }

            var to = from.AddMonths(1);
            query = query.Where(m => m.SortDate >= from && m.SortDate < to);
            query = scope.Ascending ? query.OrderBy(m => m.SortDate).ThenBy(m => m.Id) : query.OrderByDescending(m => m.SortDate).ThenByDescending(m => m.Id);
        }

        var rows = await query.Skip(offset).Take(limit)
            .Select(m => new { m.Id, m.Width, m.Height, m.ContentHash, m.Type, m.DurationMs, m.Traits, m.SortDate, m.ThumbState })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var favorites = (await db.Favorites.Where(f => f.UserId == userId && ids.Contains(f.MediaId)).Select(f => f.MediaId).ToListAsync(ct)).ToHashSet();
        return rows.Select(r => new GridItem(r.Id, r.Width, r.Height, r.ContentHash, r.Type, r.DurationMs, (r.Traits & MediaTraits.LivePhoto) != 0, favorites.Contains(r.Id), r.SortDate,
            r.ThumbState == ThumbState.Ready)).ToList();
    }

    /// <summary>The details of one photo for the viewer; null when the person may not see it.</summary>
    public static async Task<MediaDetail?> DetailAsync(AppDbContext db, long userId, long mediaId, CancellationToken ct = default)
    {
        var grants = await StorageAccess.ListForPersonAsync(db, userId, ct);
        var visible = grants.Where(g => g.IsLibrary).Select(g => g.Id).ToArray();
        var m = await db.Media.AsNoTracking().Include(x => x.Album).Include(x => x.Storage).FirstOrDefaultAsync(x => x.Id == mediaId && visible.Contains(x.StorageId), ct);
        if (m is null)
        {
            return null;
        }

        var favorite = await db.Favorites.AnyAsync(f => f.UserId == userId && f.MediaId == mediaId, ct);
        var lists = await db.CollectionItems.Where(i => i.MediaId == mediaId && (i.Collection!.OwnerId == userId || i.Collection.IsShared))
            .OrderBy(i => i.Collection!.Name).Select(i => new { i.CollectionId, i.Collection!.Name }).ToListAsync(ct);
        var canEdit = grants.Any(g => g.Id == m.StorageId && g.Role >= AccessRole.Editor);
        var format = MediaFormats.Of(m.FileName);
        return new MediaDetail(m.Id, m.FileName, m.Path, m.Type, m.Traits, m.MimeType, m.Size, m.Width, m.Height, m.TakenDate, m.DateSource, m.SortDate, m.CameraMake, m.CameraModel,
            m.LensModel, m.FNumber, m.ExposureTime, m.Iso, m.FocalLength, m.Latitude, m.Longitude, m.Altitude, m.City, m.Region, m.Country, m.CountryCode, m.DurationMs, m.VideoCodec,
            m.LiveVideoId, m.ContentHash, m.ThumbState == ThumbState.Ready, m.AlbumId, m.Album?.Name ?? string.Empty, m.StorageId, m.Storage?.Name ?? string.Empty, canEdit, favorite,
            lists.Select(l => (l.CollectionId, l.Name)).ToList(), format?.BrowserCanShow ?? false, await Faces.PeopleService.OnPhotoAsync(db, mediaId, ct));
    }

    /// <summary>The record of a photo the person may see (to read its file or its thumbnail); null otherwise.</summary>
    public static async Task<Media?> FindAsync(AppDbContext db, long userId, long mediaId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        return await db.Media.AsNoTracking().Include(m => m.Storage).FirstOrDefaultAsync(m => m.Id == mediaId && visible.Contains(m.StorageId), ct);
    }

    /// <summary>The content hashes of several photos the person may see (the thumbnails of a whole page in one query).</summary>
    public static async Task<Dictionary<long, string>> HashesAsync(AppDbContext db, long userId, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        return await db.Media.AsNoTracking().Where(m => ids.Contains(m.Id) && visible.Contains(m.StorageId)).ToDictionaryAsync(m => m.Id, m => m.ContentHash, ct);
    }

    /// <summary>Marks or unmarks photos as favourites of the person (only photos they may see). Answers how many changed.</summary>
    public static async Task<int> SetFavoriteAsync(AppDbContext db, long userId, IReadOnlyCollection<long> mediaIds, bool favorite, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var ids = await db.Media.Where(m => mediaIds.Contains(m.Id) && visible.Contains(m.StorageId)).Select(m => m.Id).ToListAsync(ct);
        if (!favorite)
        {
            return await db.Favorites.Where(f => f.UserId == userId && ids.Contains(f.MediaId)).ExecuteDeleteAsync(ct);
        }

        var already = await db.Favorites.Where(f => f.UserId == userId && ids.Contains(f.MediaId)).Select(f => f.MediaId).ToListAsync(ct);
        var added = ids.Except(already).ToList();
        db.Favorites.AddRange(added.Select(id => new MediaFavorite { UserId = userId, MediaId = id }));
        await db.SaveChangesAsync(ct);
        return added.Count;
    }

    /// <summary>The automatic albums: places (towns and countries), types, cameras and years, each with its number of photos and a cover.</summary>
    public static async Task<ExploreModel> ExploreAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var query = Shown(db, visible);

        var cities = await query.Where(m => m.City != null && m.CountryCode != null)
            .GroupBy(m => new { m.CountryCode, m.City, m.Country })
            .Select(g => new { g.Key.CountryCode, g.Key.City, g.Key.Country, Count = g.Count(), Cover = g.Max(m => m.Id) })
            .OrderByDescending(g => g.Count).Take(48).ToListAsync(ct);
        var countries = await query.Where(m => m.CountryCode != null)
            .GroupBy(m => new { m.CountryCode, m.Country })
            .Select(g => new { g.Key.CountryCode, g.Key.Country, Count = g.Count(), Cover = g.Max(m => m.Id) })
            .OrderByDescending(g => g.Count).Take(48).ToListAsync(ct);
        var cameras = await query.Where(m => m.CameraModel != null)
            .GroupBy(m => new { m.CameraModel, m.CameraMake })
            .Select(g => new { g.Key.CameraModel, g.Key.CameraMake, Count = g.Count(), Cover = g.Max(m => m.Id) })
            .OrderByDescending(g => g.Count).Take(24).ToListAsync(ct);
        var years = await query.GroupBy(m => m.SortDate.Year)
            .Select(g => new { Year = g.Key, Count = g.Count(), Cover = g.Max(m => m.Id) })
            .OrderByDescending(g => g.Year).ToListAsync(ct);

        var types = new List<(string Kind, string Icon, int Count, long? Cover)>();
        foreach (var kind in MediaScope.TypeKinds)
        {
            var typed = await QueryAsync(db, userId, new MediaScope(kind), ct);
            var row = await typed!.GroupBy(_ => 1).Select(g => new { Count = g.Count(), Cover = g.Max(m => m.Id) }).FirstOrDefaultAsync(ct);
            if (row is { Count: > 0 })
            {
                types.Add((kind, TypeIcon(kind), row.Count, row.Cover));
            }
        }

        var covers = cities.Select(c => c.Cover).Concat(countries.Select(c => c.Cover)).Concat(cameras.Select(c => c.Cover)).Concat(years.Select(y => y.Cover))
            .Concat(types.Select(t => t.Cover ?? 0)).Distinct().ToList();
        var hashes = await db.Media.Where(m => covers.Contains(m.Id) && m.ThumbState == ThumbState.Ready).ToDictionaryAsync(m => m.Id, m => m.ContentHash, ct);
        ExploreCard Card(string scope, string title, string? subtitle, int count, long? cover, string icon)
            => new(scope, title, subtitle, count, cover, cover is { } c && hashes.TryGetValue(c, out var h) ? h : null, icon);

        return new ExploreModel(
            cities.Select(c => Card($"place:{c.CountryCode}/{c.City}", c.City!, GeoPlaces.CountryName(c.CountryCode, c.Country), c.Count, c.Cover, "map-pin")).ToList(),
            countries.Select(c => Card($"place:{c.CountryCode}", GeoPlaces.CountryName(c.CountryCode, c.Country), null, c.Count, c.Cover, "globe")).ToList(),
            types.Select(t => Card(t.Kind, Localization.Text.T("Explore.Type." + t.Kind), null, t.Count, t.Cover, t.Icon)).ToList(),
            cameras.Select(c => Card($"camera:{c.CameraModel}", c.CameraModel!, c.CameraMake, c.Count, c.Cover, "camera")).ToList(),
            years.Select(y => Card($"year:{y.Year}", y.Year.ToString(CultureInfo.InvariantCulture), null, y.Count, y.Cover, "calendar")).ToList());
    }

    /// <summary>The title of a scope for the head of its page (an album and a list are named by their pages).</summary>
    public static string TitleOf(MediaScope scope) => scope.Kind switch
    {
        "favorites" => Localization.Text.T("Favorites.Title"),
        "place" => scope.Place.City ?? GeoPlaces.CountryName(scope.Place.Country),
        "camera" => scope.Value ?? string.Empty,
        "year" => (scope.Id ?? 0).ToString(CultureInfo.InvariantCulture),
        "search" => Localization.Text.T("Search.ResultsFor", scope.Value ?? string.Empty),
        _ when MediaScope.TypeKinds.Contains(scope.Kind) => Localization.Text.T("Explore.Type." + scope.Kind),
        _ => Localization.Text.T("Photos.Title"),
    };

    public static string TypeIcon(string kind) => kind switch
    {
        "videos" => "video",
        "screenshots" => "screenshot",
        "selfies" => "selfie",
        "panoramas" => "panorama",
        "live" => "live",
        "animated" => "animated",
        "raw" => "aperture",
        "nodate" => "calendar",
        "noplace" => "map-pin",
        _ => "image",
    };

    /// <summary>How many photos and videos the person sees, and how many are still being read (the head of the timeline).</summary>
    public static async Task<(int Photos, int Videos, int Pending)> CountsAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var shown = Shown(db, visible);
        var photos = await shown.CountAsync(m => m.Type == MediaType.Photo, ct);
        var videos = await shown.CountAsync(m => m.Type == MediaType.Video, ct);
        var pending = await db.Media.CountAsync(m => visible.Contains(m.StorageId) && m.ThumbState == ThumbState.Pending, ct);
        return (photos, videos, pending);
    }

    /// <summary>What the grids show of the storages <paramref name="visible"/>: no video half of a Live Photo, no file that was never read (one that waits for new thumbnails stays).</summary>
    internal static IQueryable<Media> Shown(AppDbContext db, long[] visible)
        => db.Media.AsNoTracking().Where(m => visible.Contains(m.StorageId) && (m.Traits & MediaTraits.LiveVideo) == 0 && (m.ThumbState != ThumbState.Pending || m.ContentHash != string.Empty));

    /// <summary>
    /// The search: every word must be found (a year, "video", "screenshot", "favourite" are understood); a word is looked for in the name of the file, of the album, the
    /// place, the camera and the people on the photo.
    /// </summary>
    internal static IQueryable<Media> Search(AppDbContext db, IQueryable<Media> query, string text)
    {
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8))
        {
            var word = raw.ToLowerInvariant();
            if (word.Length == 4 && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1800 and <= 2200)
            {
                query = query.Where(m => m.SortDate.Year == year);
                continue;
            }

            if (word is "video" or "videos")
            {
                query = query.Where(m => m.Type == MediaType.Video);
                continue;
            }

            if (word is "screenshot" or "screenshots" or "bildschirmfoto" or "bildschirmfotos")
            {
                query = query.Where(m => (m.Traits & MediaTraits.Screenshot) != 0);
                continue;
            }

            if (word is "selfie" or "selfies")
            {
                query = query.Where(m => (m.Traits & MediaTraits.Selfie) != 0);
                continue;
            }

            var like = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(m => EF.Functions.ILike(m.FileName, like) || EF.Functions.ILike(m.Album!.Name, like) || EF.Functions.ILike(m.City ?? string.Empty, like)
                || EF.Functions.ILike(m.Country ?? string.Empty, like) || EF.Functions.ILike(m.Region ?? string.Empty, like) || EF.Functions.ILike(m.CameraModel ?? string.Empty, like)
                || db.Faces.Any(f => f.MediaId == m.Id && !f.IsIgnored && f.Person!.Name != null && EF.Functions.ILike(f.Person.Name, like)));
        }

        return query;
    }

    private static bool TryMonth(string bucket, out DateTime from)
    {
        from = default;
        if (bucket.Length != 7 || bucket[4] != '-'
            || !int.TryParse(bucket[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(bucket[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var month) || month is < 1 or > 12 || year is < 1 or > 9998)
        {
            return false;
        }

        from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return true;
    }
}

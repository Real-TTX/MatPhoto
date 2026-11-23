using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Faces;

/// <summary>A person as a card: the name (null = not named yet), how many photos the viewer sees of them, the face that shows them.</summary>
public sealed record PersonCard(long Id, string? Name, int Photos, long? CoverFaceId, bool Hidden);

public sealed record PeopleCriteria(string? Search, string? Filter, string Sort, bool Descending, int Page, int PageSize);

public sealed record PeopleList(IReadOnlyList<PersonCard> Items, int Total, int Matching);

/// <summary>One person for their page: the name, the photos the viewer sees, whether they may change the person.</summary>
public sealed record PersonDetail(long Id, string? Name, bool Hidden, int Photos, long? CoverFaceId, bool CanEdit);

/// <summary>A face of a person (the page Edit: "not this person").</summary>
public sealed record PersonFace(long Id, long MediaId, bool Confirmed);

/// <summary>A face on a photo (the info of the viewer): of a person (<paramref name="Id"/>, a name or not yet), or of nobody yet ("who is this?").</summary>
public sealed record PhotoPerson(long? Id, string? Name, long FaceId);

/// <summary>What the person changes on the page Edit of a person.</summary>
public sealed record PersonInput(string? Name, bool Hidden, long? MergeIntoId);

/// <summary>How far the recognition is (the admin page System, tab People).</summary>
public sealed record PeopleStats(bool Enabled, bool ModelsThere, int Photos, int Looked, int Failed, int Faces, int People, int Named);

/// <summary>
/// The people of the library, as everybody sees them: a person exists for a viewer only on the photos they may see (<see cref="StorageAccess.VisibleIdsAsync"/>),
/// counts are of those photos, and a person they see no face of answers null (404). A person may be named, hidden, merged and forgotten, and a face may be taken
/// from a person, by whoever sees a face of that person (a name changes no file): a name is the same for every person of the installation.
/// </summary>
public static class PeopleService
{
    /// <summary>The faces a viewer may see: of photos of their storages that are shown, never the ones a person said belong to nobody.</summary>
    private static IQueryable<Face> VisibleFaces(AppDbContext db, long[] visible)
        => db.Faces.AsNoTracking().Where(f => !f.IsIgnored && visible.Contains(f.Media!.StorageId) && (f.Media.Traits & MediaTraits.LiveVideo) == 0);

    public static async Task<PeopleList> ListAsync(AppDbContext db, long userId, PeopleCriteria c, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var rows = await VisibleFaces(db, visible).Where(f => f.PersonId != null)
            .GroupBy(f => f.PersonId!.Value)
            .Select(g => new { PersonId = g.Key, Photos = g.Select(f => f.MediaId).Distinct().Count(), AnyFace = g.Max(f => f.Id) })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.PersonId).ToList();
        var people = await db.People.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var cards = rows.Where(r => people.ContainsKey(r.PersonId)).Select(r =>
        {
            var p = people[r.PersonId];
            return new PersonCard(p.Id, p.Name, r.Photos, p.CoverFaceId ?? r.AnyFace, p.IsHidden);
        }).ToList();

        // a cover the viewer cannot see is replaced by a face they can see
        var covers = cards.Where(x => x.CoverFaceId is not null).Select(x => x.CoverFaceId!.Value).ToList();
        var seen = (await VisibleFaces(db, visible).Where(f => covers.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        cards = cards.Select(x => x.CoverFaceId is { } f && !seen.Contains(f) ? x with { CoverFaceId = rows.First(r => r.PersonId == x.Id).AnyFace } : x).ToList();

        var total = cards.Count(x => !x.Hidden);
        IEnumerable<PersonCard> query = c.Filter switch
        {
            "named" => cards.Where(x => !x.Hidden && x.Name is not null),
            "unnamed" => cards.Where(x => !x.Hidden && x.Name is null),
            "hidden" => cards.Where(x => x.Hidden),
            _ => cards.Where(x => !x.Hidden),
        };
        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            var search = c.Search.Trim();
            query = query.Where(x => x.Name is not null && x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        // named people first, then the ones with the most photos (the ones worth naming)
        query = c.Sort == "name"
            ? (c.Descending ? query.OrderByDescending(x => x.Name ?? "￿") : query.OrderBy(x => x.Name is null).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            : (c.Descending ? query.OrderBy(x => x.Name is null).ThenByDescending(x => x.Photos) : query.OrderBy(x => x.Name is null).ThenBy(x => x.Photos));
        var matching = query.ToList();
        return new PeopleList(matching.Skip((Math.Max(1, c.Page) - 1) * c.PageSize).Take(c.PageSize).ToList(), total, matching.Count);
    }

    /// <summary>The people with the most photos (named first): Explore.</summary>
    public static async Task<IReadOnlyList<PersonCard>> TopAsync(AppDbContext db, long userId, int take, CancellationToken ct = default)
        => (await ListAsync(db, userId, new PeopleCriteria(null, null, "count", true, 1, take), ct)).Items;

    public static async Task<PersonDetail?> GetAsync(AppDbContext db, long userId, long personId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, ct);
        if (person is null)
        {
            return null;
        }

        var faces = VisibleFaces(db, visible).Where(f => f.PersonId == personId);
        var photos = await faces.Select(f => f.MediaId).Distinct().CountAsync(ct);
        if (photos == 0)
        {
            return null;
        }

        var cover = person.CoverFaceId is { } c && await faces.AnyAsync(f => f.Id == c, ct) ? c : await faces.MaxAsync(f => (long?)f.Id, ct);
        return new PersonDetail(person.Id, person.Name, person.IsHidden, photos, cover, await CanEditAsync(db, userId, personId, ct));
    }

    /// <summary>The faces of a person the viewer sees, newest first (the page Edit).</summary>
    public static async Task<IReadOnlyList<PersonFace>> FacesAsync(AppDbContext db, long userId, long personId, int take, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        return await VisibleFaces(db, visible).Where(f => f.PersonId == personId).OrderByDescending(f => f.Media!.SortDate).ThenByDescending(f => f.Id).Take(take)
            .Select(f => new PersonFace(f.Id, f.MediaId, f.IsConfirmed)).ToListAsync(ct);
    }

    /// <summary>The named people the viewer sees (the suggestions when a person is named).</summary>
    public static async Task<IReadOnlyList<(long Id, string Name)>> NamesAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var ids = VisibleFaces(db, visible).Where(f => f.PersonId != null).Select(f => f.PersonId!.Value).Distinct();
        var rows = await db.People.AsNoTracking().Where(p => p.Name != null && ids.Contains(p.Id)).OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name!)).ToList();
    }

    /// <summary>The most faces of nobody shown on a photo (the largest: a crowd behind is not worth naming).</summary>
    private const int UnknownShown = 6;

    /// <summary>
    /// The faces on a photo the viewer sees (the info of the viewer), left to right: the people on it (each once, not the hidden ones), then the largest faces that
    /// belong to nobody yet, to be named there.
    /// </summary>
    public static async Task<IReadOnlyList<PhotoPerson>> OnPhotoAsync(AppDbContext db, long mediaId, CancellationToken ct = default)
    {
        var rows = await db.Faces.AsNoTracking().Where(f => f.MediaId == mediaId && !f.IsIgnored && (f.PersonId == null || !f.Person!.IsHidden))
            .Select(f => new { f.PersonId, Name = f.Person == null ? null : f.Person.Name, f.Id, f.X, f.Width }).ToListAsync(ct);
        var known = rows.Where(r => r.PersonId != null).OrderBy(r => r.X).DistinctBy(r => r.PersonId);
        var unknown = rows.Where(r => r.PersonId == null).OrderByDescending(r => r.Width).Take(UnknownShown);
        return known.Concat(unknown).OrderBy(r => r.X).Select(r => new PhotoPerson(r.PersonId, r.Name, r.Id)).ToList();
    }

    /// <summary>
    /// "This is …": faces the viewer sees go to the person of the name (a name nobody has yet makes a new person) and stay there – confirmed, the grouping never
    /// moves them. A face taken from another person is not put back there; a person left without faces is gone. Answers the person, or why not.
    /// </summary>
    public static async Task<Outcome<long>> AssignAsync(AppDbContext db, long userId, IReadOnlyCollection<long> faceIds, string? name, CancellationToken ct = default)
    {
        var clean = Clean(name);
        if (clean is null)
        {
            return Outcome<long>.Fail(Text.T("People.Error.Name"));
        }

        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var faces = await db.Faces.Where(f => faceIds.Contains(f.Id) && visible.Contains(f.Media!.StorageId)).ToListAsync(ct);
        if (faces.Count == 0)
        {
            return Outcome<long>.Denied();
        }

        var lower = clean.ToLower();
        var person = await db.People.FirstOrDefaultAsync(p => p.Name != null && p.Name.ToLower() == lower, ct);
        if (person is null)
        {
            person = db.People.Add(new Person { Name = clean }).Entity;
            await db.SaveChangesAsync(ct);
        }

        var ids = faces.Select(f => f.Id).ToList();
        await db.FaceRejections.Where(r => ids.Contains(r.FaceId) && r.PersonId == person.Id).ExecuteDeleteAsync(ct);
        var left = new HashSet<long>();
        foreach (var face in faces.Where(f => f.PersonId != person.Id || !f.IsConfirmed || f.IsIgnored))
        {
            if (face.PersonId is { } from && from != person.Id)
            {
                left.Add(from);
                db.FaceRejections.Add(new FaceRejection { FaceId = face.Id, PersonId = from });
            }

            face.PersonId = person.Id;
            face.IsConfirmed = true;
            face.IsIgnored = false;
            face.Clustered = true;
        }

        person.CoverFaceId ??= faces[0].Id;
        person.UpdateState = SoftEntity.Updated;
        ActivityLog.Add(db, userId, "person", clean, Text.T("People.Activity.Assigned", faces.Count));
        await db.SaveChangesAsync(ct);
        await TidyAsync(db, left, ids, ct);
        return Outcome<long>.Success(person.Id);
    }

    /// <summary>"Nobody to name": the face leaves its person and is not shown any more (someone in the background). False when the viewer does not see it.</summary>
    public static async Task<bool> IgnoreAsync(AppDbContext db, long userId, long faceId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var face = await db.Faces.FirstOrDefaultAsync(f => f.Id == faceId && visible.Contains(f.Media!.StorageId), ct);
        if (face is null)
        {
            return false;
        }

        var from = face.PersonId;
        face.PersonId = null;
        face.IsIgnored = true;
        face.IsConfirmed = false;
        face.Clustered = true;
        await db.SaveChangesAsync(ct);
        await TidyAsync(db, from is { } id ? [id] : [], [faceId], ct);
        return true;
    }

    /// <summary>The people faces were taken from: gone when no face is left, else a cover that left is chosen anew.</summary>
    private static async Task TidyAsync(AppDbContext db, IReadOnlyCollection<long> people, IReadOnlyCollection<long> faceIds, CancellationToken ct)
    {
        foreach (var person in await db.People.Where(p => people.Contains(p.Id)).ToListAsync(ct))
        {
            if (!await db.Faces.AnyAsync(f => f.PersonId == person.Id && !f.IsIgnored, ct))
            {
                person.UpdateState = SoftEntity.Deleted;
            }
            else if (person.CoverFaceId is { } cover && faceIds.Contains(cover))
            {
                person.CoverFaceId = null;
                person.UpdateState = SoftEntity.Updated;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Where a face is and the picture it is on (its thumbnail), if the viewer may see it; else null.</summary>
    public static async Task<(string Hash, float X, float Y, float Width, float Height)?> FaceAsync(AppDbContext db, long userId, long faceId, CancellationToken ct = default)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var face = await db.Faces.AsNoTracking().Where(f => f.Id == faceId && visible.Contains(f.Media!.StorageId))
            .Select(f => new { f.Media!.ContentHash, f.X, f.Y, f.Width, f.Height }).FirstOrDefaultAsync(ct);
        return face is null ? null : (face.ContentHash, face.X, face.Y, face.Width, face.Height);
    }

    /// <summary>
    /// Names a person (or takes the name away with an empty one). A name another person has already joins the two: the faces go to that person, which is the
    /// answer then; else the person itself. Null when the viewer may not change the person.
    /// </summary>
    public static async Task<Outcome<long>> RenameAsync(AppDbContext db, long userId, long personId, string? name, CancellationToken ct = default)
    {
        var person = await EditableAsync(db, userId, personId, ct);
        if (person is null)
        {
            return Outcome<long>.Denied();
        }

        var clean = Clean(name);
        if (clean is not null)
        {
            var lower = clean.ToLower();
            var same = await db.People.Where(p => p.Id != personId && p.Name != null && p.Name.ToLower() == lower).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct);
            if (same is { } target)
            {
                return await MergeAsync(db, userId, personId, target, ct);
            }
        }

        var before = person.Name;
        person.Name = clean;
        person.UpdateState = SoftEntity.Updated;
        ActivityLog.Add(db, userId, "person", clean ?? before, before is null ? null : Text.T("People.Activity.Renamed", before));
        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(person.Id);
    }

    /// <summary>The page Edit: the name, hidden or not, and joined into another person if one was chosen.</summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, long userId, long personId, PersonInput input, CancellationToken ct = default)
    {
        var person = await EditableAsync(db, userId, personId, ct);
        if (person is null)
        {
            return Outcome<long>.Denied();
        }

        person.IsHidden = input.Hidden;
        person.UpdateState = SoftEntity.Updated;
        await db.SaveChangesAsync(ct);
        if (input.MergeIntoId is { } into && into > 0 && into != personId)
        {
            return await MergeAsync(db, userId, personId, into, ct);
        }

        return await RenameAsync(db, userId, personId, input.Name, ct);
    }

    /// <summary>Joins a person into another (the same person in two groups): the faces go there, the first person is gone. The answer is the person that stays.</summary>
    public static async Task<Outcome<long>> MergeAsync(AppDbContext db, long userId, long personId, long intoId, CancellationToken ct = default)
    {
        var from = await EditableAsync(db, userId, personId, ct);
        var into = await EditableAsync(db, userId, intoId, ct);
        if (from is null || into is null)
        {
            return Outcome<long>.Denied();
        }

        await db.Faces.Where(f => f.PersonId == personId).ExecuteUpdateAsync(u => u.SetProperty(f => f.PersonId, intoId), ct);
        await db.FaceRejections.Where(r => r.PersonId == personId).ExecuteUpdateAsync(u => u.SetProperty(r => r.PersonId, intoId), ct);
        into.Name ??= from.Name;
        into.CoverFaceId ??= from.CoverFaceId;
        into.UpdateState = SoftEntity.Updated;
        from.UpdateState = SoftEntity.Deleted;
        ActivityLog.Add(db, userId, "person", into.Name ?? Text.T("People.Unnamed"), Text.T("People.Activity.Merged", from.Name ?? Text.T("People.Unnamed")));
        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(intoId);
    }

    /// <summary>"Not this person": the faces leave the person and are never put there again. Answers how many left.</summary>
    public static async Task<int> RejectAsync(AppDbContext db, long userId, long personId, IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        if (await EditableAsync(db, userId, personId, ct) is null || faceIds.Count == 0)
        {
            return 0;
        }

        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        var faces = await db.Faces.Where(f => f.PersonId == personId && faceIds.Contains(f.Id) && visible.Contains(f.Media!.StorageId)).ToListAsync(ct);
        foreach (var face in faces)
        {
            face.PersonId = null;
            face.IsConfirmed = false;
            face.Clustered = true;
            db.FaceRejections.Add(new FaceRejection { FaceId = face.Id, PersonId = personId });
        }

        await db.SaveChangesAsync(ct);
        return faces.Count;
    }

    /// <summary>Forgets a person (nobody worth naming): their faces stay without a person and are never grouped again.</summary>
    public static async Task<bool> ForgetAsync(AppDbContext db, long userId, long personId, CancellationToken ct = default)
    {
        var person = await EditableAsync(db, userId, personId, ct);
        if (person is null)
        {
            return false;
        }

        await db.Faces.Where(f => f.PersonId == personId).ExecuteUpdateAsync(u => u.SetProperty(f => f.PersonId, (long?)null).SetProperty(f => f.IsIgnored, true), ct);
        person.UpdateState = SoftEntity.Deleted;
        ActivityLog.Add(db, userId, "person", person.Name ?? Text.T("People.Unnamed"), Text.T("People.Activity.Forgotten"));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The face that shows the person from now on.</summary>
    public static async Task<bool> SetCoverAsync(AppDbContext db, long userId, long personId, long faceId, CancellationToken ct = default)
    {
        var person = await EditableAsync(db, userId, personId, ct);
        if (person is null || !await db.Faces.AnyAsync(f => f.Id == faceId && f.PersonId == personId, ct))
        {
            return false;
        }

        person.CoverFaceId = faceId;
        person.UpdateState = SoftEntity.Updated;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The numbers of the recognition for the administration.</summary>
    public static async Task<PeopleStats> StatsAsync(AppDbContext db, bool enabled, bool modelsThere, CancellationToken ct = default)
    {
        var photos = Eligible(db);
        return new PeopleStats(enabled, modelsThere,
            await photos.CountAsync(ct),
            await photos.CountAsync(m => m.FaceState == FaceState.Done, ct),
            await photos.CountAsync(m => m.FaceState == FaceState.Failed, ct),
            await db.Faces.CountAsync(f => !f.IsIgnored, ct),
            await db.People.CountAsync(ct),
            await db.People.CountAsync(p => p.Name != null, ct));
    }

    /// <summary>Removes every face and every person: what the recognition learnt is gone (switching it on again starts from the beginning).</summary>
    public static async Task DeleteAllAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        await db.FaceRejections.ExecuteDeleteAsync(ct);
        await db.Faces.ExecuteDeleteAsync(ct);
        await db.People.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await db.Media.IgnoreQueryFilters().Where(m => m.FaceState != FaceState.Pending).ExecuteUpdateAsync(u => u.SetProperty(m => m.FaceState, FaceState.Pending), ct);
        ActivityLog.Add(db, userId, "person", Text.T("People.Title"), Text.T("People.Activity.DeletedAll"));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The photos the recognition looks at: photos (no videos, no screenshots) whose thumbnails are made.</summary>
    internal static IQueryable<Media> Eligible(AppDbContext db)
        => db.Media.Where(m => m.Type == MediaType.Photo && (m.Traits & MediaTraits.Screenshot) == 0 && m.ThumbState == ThumbState.Ready);

    /// <summary>A name as it is kept: trimmed, at most 200 characters; empty = no name.</summary>
    public static string? Clean(string? name)
    {
        var text = string.Join(' ', (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text.Length > 200 ? text[..200] : text;
    }

    /// <summary>
    /// May the viewer change the person: whoever sees one of its faces (a name changes no file – like a favourite or a list, a viewer of a storage may do it).
    /// </summary>
    private static async Task<bool> CanEditAsync(AppDbContext db, long userId, long personId, CancellationToken ct)
    {
        var visible = await StorageAccess.VisibleIdsAsync(db, userId, ct);
        return await VisibleFaces(db, visible).AnyAsync(f => f.PersonId == personId, ct);
    }

    private static async Task<Person?> EditableAsync(AppDbContext db, long userId, long personId, CancellationToken ct)
        => await CanEditAsync(db, userId, personId, ct) ? await db.People.FirstOrDefaultAsync(p => p.Id == personId, ct) : null;
}

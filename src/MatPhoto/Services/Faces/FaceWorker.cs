using MatPhoto.Data;
using MatPhoto.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Faces;

/// <summary>
/// Looks for the faces of the photos in the background, while the recognition is switched on (<see cref="LibraryOptions.FacesEnabled"/>) and its models are there:
/// the newest photos first, a few at a time, from their previews in the cache (the NAS does not read the originals again). Then it groups the new faces into people
/// (<see cref="FaceGrouping"/>) – after every few hundred photos and whenever there is nothing more to look at. A photo whose file changed is looked at again.
/// </summary>
public sealed class FaceWorker(IServiceScopeFactory scopes, FaceEngine engine, ThumbnailCache cache, LibrarySettings settings, ILogger<FaceWorker> log) : BackgroundService
{
    private const int Batch = 16;

    /// <summary>The faces are grouped after this many photos (and when nothing is left).</summary>
    private const int GroupEvery = 300;

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Looks at once (a scan found new photos, the administration switched it on).</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            if (!engine.IsAvailable)
            {
                log.LogInformation("The face recognition is not available: its models are not in {Folder}.", engine.Folder);
            }

            var sinceGrouping = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var looked = 0;
                if (settings.Current.FacesEnabled && engine.IsAvailable)
                {
                    try
                    {
                        looked = await LookAsync(stoppingToken);
                        sinceGrouping += looked;
                        if ((looked == 0 && sinceGrouping > 0) || sinceGrouping >= GroupEvery || (looked == 0 && await UngroupedAsync(stoppingToken)))
                        {
                            await GroupAsync(stoppingToken);
                            sinceGrouping = 0;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "The face recognition failed for a moment.");
                        looked = 0;
                    }
                }

                if (looked == 0)
                {
                    await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    /// <summary>Looks for the faces of a few photos; answers how many photos it looked at.</summary>
    internal async Task<int> LookAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var photos = await PeopleService.Eligible(db).Where(m => m.FaceState == FaceState.Pending)
            .OrderByDescending(m => m.SortDate).Take(Batch).Select(m => new { m.Id, m.ContentHash }).ToListAsync(ct);
        foreach (var photo in photos)
        {
            var state = FaceState.Done;
            var found = new List<FoundFace>();
            try
            {
                var preview = await cache.ReadAsync(photo.ContentHash, ThumbKind.Preview, ct);
                if (preview is null)
                {
                    state = FaceState.Failed;
                }
                else
                {
                    found.AddRange(await Task.Run(() => engine.Analyze(preview), ct));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "The faces of photo {Id} could not be looked for.", photo.Id);
                state = FaceState.Failed;
            }

            // the faces of an earlier version of the file go; the new ones come
            await db.Faces.Where(f => f.MediaId == photo.Id).ExecuteDeleteAsync(ct);
            db.Faces.AddRange(found.Select(f => new Face
            {
                MediaId = photo.Id, X = f.X, Y = f.Y, Width = f.Width, Height = f.Height, Score = f.Score, Embedding = f.Embedding,
            }));
            await db.Media.Where(m => m.Id == photo.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.FaceState, state), ct);
            await db.SaveChangesAsync(ct);
        }

        return photos.Count;
    }

    private async Task<bool> UngroupedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Faces.AnyAsync(f => !f.Clustered && !f.IsIgnored, ct);
    }

    /// <summary>Groups the faces that were not grouped yet into people (new people without a name included).</summary>
    internal async Task GroupAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Faces.Where(f => !f.Clustered && !f.IsIgnored).Select(f => f.Id).ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        var faces = await db.Faces.AsNoTracking().Where(f => f.Media!.UpdateState != SoftEntity.Deleted)
            .Select(f => new { f.Id, f.PersonId, f.Embedding, f.IsConfirmed, f.IsIgnored, f.Score, f.Width, f.Height }).ToListAsync(ct);
        var rejected = (await db.FaceRejections.AsNoTracking().Select(r => new { r.FaceId, r.PersonId }).ToListAsync(ct)).Select(r => (r.FaceId, r.PersonId)).ToHashSet();
        var started = DateTime.UtcNow;
        var changes = await Task.Run(() => FaceGrouping.Group(faces.Select(f => new GroupFace(f.Id, f.PersonId, f.Embedding, f.IsConfirmed, f.IsIgnored)).ToList(), pending, rejected), ct);

        // the new people (without a name): the clearest, largest face shows them
        var quality = faces.ToDictionary(f => f.Id, f => f.Score * f.Width * f.Height);
        var created = new Dictionary<long, Person>();
        foreach (var group in changes.Where(c => c.Value < 0).GroupBy(c => c.Value))
        {
            var person = new Person { CoverFaceId = group.MaxBy(c => quality.GetValueOrDefault(c.Key)).Key };
            db.People.Add(person);
            created[group.Key] = person;
        }

        await db.SaveChangesAsync(ct);
        foreach (var group in changes.GroupBy(c => c.Value < 0 ? created[c.Value].Id : c.Value))
        {
            var ids = group.Select(c => c.Key).ToList();
            await db.Faces.Where(f => ids.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.PersonId, group.Key), ct);
        }

        await db.Faces.Where(f => pending.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.Clustered, true), ct);
        log.LogInformation("Faces grouped: {Pending} new faces, {Assigned} put to people, {Created} new people ({Ms} ms).", pending.Count, changes.Count, created.Count,
            (int)(DateTime.UtcNow - started).TotalMilliseconds);
    }
}

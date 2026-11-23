using ImageMagick;
using MatPhoto.Data;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MatPhoto.Tests;

/// <summary>
/// The library against a real PostgreSQL and a folder of photos: the scan makes albums and records, finds moved, changed and removed files, the business layer shows
/// a person only the storages they may see, albums are renamed on the disk, duplicates and lists work.
/// </summary>
public sealed class LibraryDbTests : IAsyncLifetime
{
    private TestDatabase? _database;
    private TempFolder? _folder;
    private ServiceProvider? _services;
    private string Photos => Path.Combine(_folder!.Root, "photos");

    public async Task InitializeAsync()
    {
        if (TestDatabase.Admin is null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        _folder = new TempFolder();
        Directory.CreateDirectory(Photos);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataDirectory"] = Path.Combine(_folder.Root, "data"),
            ["StorageRoot"] = _folder.Root,
        }).Build();
        var connection = _database.Db.Database.GetConnectionString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        services.AddSingleton<StorageConnections>();
        services.AddSingleton<LibrarySettings>();
        services.AddSingleton<ThumbnailCache>();
        services.AddSingleton<VideoTools>();
        services.AddSingleton<MediaIndexer>();
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<ImportWorker>();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        _folder?.Dispose();
    }

    private static void Picture(string file, string colours, DateTime taken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var image = new MagickImage($"plasma:{colours}", 640, 480);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.DateTimeOriginal, taken.ToString("yyyy:MM:dd HH:mm:ss"));
        image.SetProfile(exif);
        image.Write(file);
    }

    private async Task<(Storage Storage, User Anna, User Ben)> SetUpAsync()
    {
        var db = _database!.Db;
        var anna = await _database.AddUserAsync("Anna");
        var ben = await _database.AddUserAsync("Ben");
        var storage = new Storage { Name = "NAS", Kind = StorageKind.Local, RootPath = Photos, ForEveryone = false, IsLibrary = true };
        db.Storages.Add(storage);
        await db.SaveChangesAsync();
        db.StorageAccess.Add(new StorageAccessEntry { StorageId = storage.Id, UserId = anna.Id, Role = AccessRole.Editor });
        await db.SaveChangesAsync();

        Picture(Path.Combine(Photos, "2023.11.09 Hochzeit Julius", "IMG_1.jpg"), "#ff8040-#2040a0", new DateTime(2023, 11, 9, 14, 0, 0));
        Picture(Path.Combine(Photos, "2023.11.09 Hochzeit Julius", "IMG_2.jpg"), "#20a040-#a02080", new DateTime(2023, 11, 9, 15, 0, 0));
        Picture(Path.Combine(Photos, "2024", "2024.07.14 Gardasee", "PXL_1.jpg"), "#4060ff-#ffe020", new DateTime(2024, 7, 14, 10, 0, 0));
        Directory.CreateDirectory(Path.Combine(Photos, "@eaDir"));
        Picture(Path.Combine(Photos, "@eaDir", "SYNOPHOTO_THUMB.jpg"), "#000000-#ffffff", new DateTime(2020, 1, 1));
        return (storage, anna, ben);
    }

    private Task<ScanProgress> ScanAsync(long storageId) => _services!.GetRequiredService<LibraryScanner>().ScanAsync(storageId, CancellationToken.None);

    [DbFact]
    public async Task A_scan_makes_the_albums_and_a_person_sees_only_their_storages()
    {
        var (storage, anna, ben) = await SetUpAsync();
        var progress = await ScanAsync(storage.Id);
        Assert.Equal("done", progress.Phase);
        Assert.Equal(3, progress.Files);
        Assert.Equal(0, progress.Failed);

        var db = _database!.Db;
        db.ChangeTracker.Clear();
        var albums = await db.Albums.OrderBy(a => a.Path).ToListAsync();
        Assert.Equal(["2023.11.09 Hochzeit Julius", "2024/2024.07.14 Gardasee"], albums.Select(a => a.Path));
        Assert.Equal(new DateOnly(2023, 11, 9), albums[0].AlbumDate);
        Assert.Equal("Hochzeit Julius", albums[0].Title);
        Assert.Equal(2, albums[0].MediaCount);
        Assert.All(await db.Media.ToListAsync(), m => Assert.Equal(ThumbState.Ready, m.ThumbState));

        var buckets = await LibraryService.BucketsAsync(db, anna.Id, MediaScope.All);
        Assert.Equal(["2024-07", "2023-11"], buckets!.Select(b => b.Key));
        Assert.Empty((await LibraryService.BucketsAsync(db, ben.Id, MediaScope.All))!);
        Assert.Null(await LibraryService.BucketsAsync(db, ben.Id, new MediaScope("album", albums[0].Id)));
        var media = await db.Media.FirstAsync();
        Assert.Null(await LibraryService.DetailAsync(db, ben.Id, media.Id));
        Assert.NotNull(await LibraryService.DetailAsync(db, anna.Id, media.Id));
        Assert.Equal(0, await LibraryService.SetFavoriteAsync(db, ben.Id, [media.Id], true));
    }

    [DbFact]
    public async Task A_moved_file_keeps_its_record_and_a_removed_one_goes()
    {
        var (storage, anna, _) = await SetUpAsync();
        await ScanAsync(storage.Id);
        var db = _database!.Db;
        var moved = await db.Media.SingleAsync(m => m.FileName == "IMG_2.jpg");
        Assert.Equal(1, await LibraryService.SetFavoriteAsync(db, anna.Id, [moved.Id], true));

        Directory.CreateDirectory(Path.Combine(Photos, "2023.11.10 Party"));
        File.Move(Path.Combine(Photos, "2023.11.09 Hochzeit Julius", "IMG_2.jpg"), Path.Combine(Photos, "2023.11.10 Party", "IMG_2.jpg"));
        File.Delete(Path.Combine(Photos, "2023.11.09 Hochzeit Julius", "IMG_1.jpg"));
        var progress = await ScanAsync(storage.Id);
        Assert.Equal(1, progress.Removed);

        db.ChangeTracker.Clear();
        var again = await db.Media.SingleAsync(m => m.FileName == "IMG_2.jpg");
        Assert.Equal(moved.Id, again.Id);
        Assert.Equal("2023.11.10 Party/IMG_2.jpg", again.Path);
        Assert.True(await db.Favorites.AnyAsync(f => f.MediaId == moved.Id && f.UserId == anna.Id));
        Assert.Equal(2, await db.Media.CountAsync());
        Assert.Equal(0, (await db.Albums.SingleAsync(a => a.Name == "2023.11.09 Hochzeit Julius")).MediaCount);
    }

    [DbFact]
    public async Task An_album_is_renamed_on_the_disk_and_moved_to_the_trash_folder()
    {
        var (storage, anna, ben) = await SetUpAsync();
        await ScanAsync(storage.Id);
        var db = _database!.Db;
        var connections = _services!.GetRequiredService<StorageConnections>();
        var options = _services!.GetRequiredService<LibrarySettings>().Reload(new SystemSetting());
        var album = await db.Albums.SingleAsync(a => a.Name == "2023.11.09 Hochzeit Julius");

        var (found, _) = await AlbumService.SaveAsync(db, connections, options, ben.Id, album.Id, new AlbumInput(storage.Id, null, new DateOnly(2023, 11, 9), "Hochzeit", null, null));
        Assert.False(found);

        var (ok, errors) = await AlbumService.SaveAsync(db, connections, options, anna.Id, album.Id, new AlbumInput(storage.Id, null, new DateOnly(2023, 11, 9), "Hochzeit von Julius", null, null));
        Assert.True(ok);
        Assert.Empty(errors);
        Assert.True(Directory.Exists(Path.Combine(Photos, "2023.11.09 Hochzeit von Julius")));
        db.ChangeTracker.Clear();
        Assert.All(await db.Media.Where(m => m.AlbumId == album.Id).ToListAsync(), m => Assert.StartsWith("2023.11.09 Hochzeit von Julius/", m.Path, StringComparison.Ordinal));

        var trashed = await AlbumService.DeleteAsync(db, connections, anna.Id, album.Id);
        Assert.True(trashed.Ok, trashed.Error);
        Assert.False(Directory.Exists(Path.Combine(Photos, "2023.11.09 Hochzeit von Julius")));
        Assert.Single(Directory.GetDirectories(Path.Combine(Photos, MediaFormats.TrashFolder)));
        Assert.Equal(1, await db.Media.CountAsync());
    }

    [DbFact]
    public async Task An_import_of_a_library_into_itself_sorts_it_in_place()
    {
        var (storage, anna, _) = await SetUpAsync();
        Picture(Path.Combine(Photos, "DCIM", "100CANON", "IMG_9.jpg"), "#e0e020-#202080", new DateTime(2025, 5, 1, 12, 0, 0));
        Picture(Path.Combine(Photos, "DCIM", "100CANON", "@eaDir", "IMG_9.jpg", "SYNOPHOTO_THUMB_M.jpg"), "#000000-#ffffff", new DateTime(2020, 1, 1));
        File.WriteAllText(Path.Combine(Photos, "DCIM", "100CANON", "Thumbs.db"), "x");
        Picture(Path.Combine(Photos, "2010 Urlaub", "20100127", "a.jpg"), "#a0a0a0-#20e020", new DateTime(2010, 1, 27, 9, 0, 0));
        File.WriteAllText(Path.Combine(Photos, "2010 Urlaub", "Notizen.txt"), "Packliste");
        _services!.GetRequiredService<LibrarySettings>().Reload(new SystemSetting());
        await ScanAsync(storage.Id);
        var db = _database!.Db;
        var favourite = await db.Media.SingleAsync(m => m.FileName == "IMG_9.jpg");
        await LibraryService.SetFavoriteAsync(db, anna.Id, [favourite.Id], true);

        // the whole library into itself, "copy" chosen: it is moved all the same, to the top of the storage
        var created = await ImportService.CreateFromStorageAsync(db, anna.Id, storage.Id, string.Empty, move: false);
        Assert.True(created.Ok);
        var worker = _services!.GetRequiredService<ImportWorker>();
        var session = await db.ImportSessions.SingleAsync(s => s.Token == created.Value);
        await worker.RunAsync(session.Id, CancellationToken.None);
        db.ChangeTracker.Clear();
        var view = (await ImportService.GetAsync(db, anna.Id, created.Value))!;
        Assert.True(view.InPlace);
        Assert.True(view.MoveFiles);
        Assert.Equal(string.Empty, view.TargetFolder);
        Assert.Empty(view.Duplicates);

        // the albums that are folders already stay; the day folders of "2010 Urlaub" become one album; the camera's folder an album of its day
        var wedding = view.Groups.Single(g => g.Items.Any(i => i.FileName == "IMG_1.jpg"));
        Assert.Equal("2023.11.09 Hochzeit Julius", wedding.Name);
        Assert.NotNull(wedding.ExistingAlbumId);
        Assert.NotNull(view.Groups.Single(g => g.Items.Any(i => i.FileName == "PXL_1.jpg")).ExistingAlbumId);
        Assert.Equal("2010.01.27 Urlaub", view.Groups.Single(g => g.Items.Any(i => i.FileName == "a.jpg")).Name);
        var camera = view.Groups.Single(g => g.Items.Any(i => i.FileName == "IMG_9.jpg"));
        Assert.Null(camera.ExistingAlbumId);

        var decision = new ImportDecision(storage.Id, view.TargetFolder, view.Groups.Select(g => new ImportGroupDecision(g.Id, g.Name, true, g.ExistingAlbumId)).ToList());
        var (found, errors) = await ImportService.DecideAsync(db, anna.Id, created.Value, decision, start: true);
        Assert.True(found);
        Assert.Empty(errors);
        await worker.RunAsync(session.Id, CancellationToken.None);
        db.ChangeTracker.Clear();
        view = (await ImportService.GetAsync(db, anna.Id, created.Value))!;
        Assert.Equal(ImportState.Done, view.State);
        Assert.Equal(5, view.ImportedCount);
        Assert.Equal(3, view.Stayed);
        Assert.Equal(0, view.FailedCount);

        // nothing beside itself, nothing twice
        Assert.Equal(["IMG_1.jpg", "IMG_2.jpg"], Directory.GetFiles(Path.Combine(Photos, "2023.11.09 Hochzeit Julius")).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(Photos, "2024", "2024.07.14 Gardasee", "PXL_1.jpg")));
        Assert.True(File.Exists(Path.Combine(Photos, "2010.01.27 Urlaub", "a.jpg")));
        var cameraFolder = camera.Name;
        Assert.True(File.Exists(Path.Combine(Photos, cameraFolder, "IMG_9.jpg")));

        // the camera's folders held only what a NAS leaves: into the trash; "2010 Urlaub" keeps its notes, only its day folder goes
        Assert.False(Directory.Exists(Path.Combine(Photos, "DCIM")));
        Assert.True(File.Exists(Path.Combine(Photos, "2010 Urlaub", "Notizen.txt")));
        Assert.False(Directory.Exists(Path.Combine(Photos, "2010 Urlaub", "20100127")));
        var trash = Directory.GetDirectories(Path.Combine(Photos, MediaFormats.TrashFolder)).Single();
        Assert.True(Directory.Exists(Path.Combine(trash, "DCIM", "100CANON", "@eaDir")));

        // the scan finds the moved files as the records they were: the favourite stays
        await ScanAsync(storage.Id);
        db.ChangeTracker.Clear();
        Assert.Equal(5, await db.Media.CountAsync());
        var moved = await db.Media.SingleAsync(m => m.FileName == "IMG_9.jpg");
        Assert.Equal(favourite.Id, moved.Id);
        Assert.Equal($"{cameraFolder}/IMG_9.jpg", moved.Path);
        Assert.True(await db.Favorites.AnyAsync(f => f.MediaId == favourite.Id && f.UserId == anna.Id));
    }

    [DbFact]
    public async Task Duplicates_and_lists()
    {
        var (storage, anna, ben) = await SetUpAsync();
        File.Copy(Path.Combine(Photos, "2023.11.09 Hochzeit Julius", "IMG_1.jpg"), Path.Combine(Photos, "2024", "2024.07.14 Gardasee", "IMG_1 Kopie.jpg"));
        await ScanAsync(storage.Id);
        var db = _database!.Db;

        var report = await DuplicateService.FindAsync(db, anna.Id, 6, "all");
        var group = Assert.Single(report.Groups);
        Assert.True(group.Exact);
        Assert.Equal(2, group.Items.Count);
        Assert.Empty((await DuplicateService.FindAsync(db, ben.Id, 6, "all")).Groups);
        await DuplicateService.DismissAsync(db, anna.Id, group.Items.Select(i => i.Id).ToList());
        Assert.Empty((await DuplicateService.FindAsync(db, anna.Id, 6, "all")).Groups);

        var (listId, _) = await CollectionService.SaveAsync(db, anna.Id, null, new CollectionInput("Girlande Max Geburtstag", null, "#b81f8a", false, null));
        var ids = await db.Media.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
        Assert.Equal(ids.Count, await CollectionService.AddAsync(db, anna.Id, listId!.Value, ids));
        Assert.Equal(0, await CollectionService.AddAsync(db, anna.Id, listId.Value, ids));
        Assert.Null(await CollectionService.AddAsync(db, ben.Id, listId.Value, ids));
        var items = await LibraryService.ItemsAsync(db, anna.Id, new MediaScope("list", listId), "all", 0, 100);
        Assert.Equal(ids, items!.Select(i => i.Id));
        Assert.Null(await LibraryService.BucketsAsync(db, ben.Id, new MediaScope("list", listId)));
    }

    [DbFact]
    public async Task Faces_become_people_a_person_names_joins_and_forgets()
    {
        var (storage, anna, ben) = await SetUpAsync();
        await ScanAsync(storage.Id);
        var db = _database!.Db;
        var photos = await db.Media.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
        var random = new Random(7);
        float[] Near(float[] centre) => Services.Faces.FaceEngine.Normalize(centre.Select(v => v + (float)(random.NextDouble() - 0.5) * 0.04f).ToArray());
        float[] Somebody() => Services.Faces.FaceEngine.Normalize(Enumerable.Range(0, 128).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());

        // one person on each of the three photos, another one on each of them too, and a stranger once
        var max = Somebody();
        var lena = Somebody();
        foreach (var photo in photos)
        {
            db.Faces.Add(new Face { MediaId = photo, X = 0.1f, Y = 0.1f, Width = 0.2f, Height = 0.2f, Score = 0.9f, Embedding = Near(max) });
            db.Faces.Add(new Face { MediaId = photo, X = 0.6f, Y = 0.1f, Width = 0.2f, Height = 0.2f, Score = 0.9f, Embedding = Near(lena) });
        }

        db.Faces.Add(new Face { MediaId = photos[0], X = 0.4f, Y = 0.5f, Width = 0.1f, Height = 0.1f, Score = 0.9f, Embedding = Somebody() });
        await db.SaveChangesAsync();
        var worker = ActivatorUtilities.CreateInstance<Services.Faces.FaceWorker>(_services!, new Services.Faces.FaceEngine(Path.Combine(_folder!.Root, "no-models")));
        await worker.GroupAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        // two people without a name; Ben sees none of them (he may not see the storage)
        var people = await Services.Faces.PeopleService.ListAsync(db, anna.Id, new Services.Faces.PeopleCriteria(null, null, "count", true, 1, 50));
        Assert.Equal(2, people.Items.Count);
        Assert.All(people.Items, p => Assert.Null(p.Name));
        Assert.All(people.Items, p => Assert.Equal(3, p.Photos));
        Assert.Empty((await Services.Faces.PeopleService.ListAsync(db, ben.Id, new Services.Faces.PeopleCriteria(null, null, "count", true, 1, 50))).Items);
        var first = people.Items[0].Id;
        var second = people.Items[1].Id;
        Assert.Null(await Services.Faces.PeopleService.GetAsync(db, ben.Id, first));
        Assert.False((await Services.Faces.PeopleService.RenameAsync(db, ben.Id, first, "Max")).Ok);

        // a name; the photos of the person and the search by the name
        Assert.Equal(first, (await Services.Faces.PeopleService.RenameAsync(db, anna.Id, first, "  Max  ")).Value);
        Assert.Equal("Max", (await Services.Faces.PeopleService.GetAsync(db, anna.Id, first))!.Name);
        Assert.Equal(3, (await LibraryService.BucketsAsync(db, anna.Id, new MediaScope("person", first)))!.Sum(b => b.Count));
        Assert.Equal(3, (await LibraryService.BucketsAsync(db, anna.Id, MediaScope.Parse("search:max")))!.Sum(b => b.Count));
        Assert.Null(await LibraryService.BucketsAsync(db, ben.Id, new MediaScope("person", first)));
        var detail = await LibraryService.DetailAsync(db, anna.Id, photos[0]);
        Assert.Contains(detail!.People, p => p.Name == "Max");

        // the same name for the other person joins them (as if it were found twice)
        Assert.Equal(first, (await Services.Faces.PeopleService.RenameAsync(db, anna.Id, second, "max")).Value);
        db.ChangeTracker.Clear();
        Assert.Equal(6, await db.Faces.CountAsync(f => f.PersonId == first));
        Assert.Null(await Services.Faces.PeopleService.GetAsync(db, anna.Id, second));

        // "not this person": the face leaves and is not grouped there again
        var wrong = await db.Faces.Where(f => f.PersonId == first).OrderBy(f => f.Id).Select(f => f.Id).FirstAsync();
        Assert.Equal(1, await Services.Faces.PeopleService.RejectAsync(db, anna.Id, first, [wrong]));
        await db.Faces.Where(f => f.Id == wrong).ExecuteUpdateAsync(u => u.SetProperty(f => f.Clustered, false));
        await worker.GroupAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Null((await db.Faces.FirstAsync(f => f.Id == wrong)).PersonId);

        // forgotten: no person, the faces are not grouped again
        Assert.True(await Services.Faces.PeopleService.ForgetAsync(db, anna.Id, first));
        await worker.GroupAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Empty((await Services.Faces.PeopleService.ListAsync(db, anna.Id, new Services.Faces.PeopleCriteria(null, null, "count", true, 1, 50))).Items);
        Assert.Equal(5, await db.Faces.CountAsync(f => f.IsIgnored));
    }

    [DbFact]
    public async Task A_face_is_named_moved_to_another_person_or_hidden_one_by_one()
    {
        var (storage, anna, ben) = await SetUpAsync();
        await ScanAsync(storage.Id);
        var db = _database!.Db;
        var photos = await db.Media.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
        var random = new Random(11);
        float[] Near(float[] centre) => Services.Faces.FaceEngine.Normalize(centre.Select(v => v + (float)(random.NextDouble() - 0.5) * 0.04f).ToArray());
        float[] Somebody() => Services.Faces.FaceEngine.Normalize(Enumerable.Range(0, 128).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());

        // three wax figures alike enough to be one "person", and a stranger once
        var figure = Somebody();
        foreach (var photo in photos)
        {
            db.Faces.Add(new Face { MediaId = photo, X = 0.1f, Y = 0.1f, Width = 0.2f, Height = 0.2f, Score = 0.9f, Embedding = Near(figure) });
        }

        db.Faces.Add(new Face { MediaId = photos[0], X = 0.5f, Y = 0.5f, Width = 0.1f, Height = 0.1f, Score = 0.9f, Embedding = Somebody() });
        await db.SaveChangesAsync();
        var worker = ActivatorUtilities.CreateInstance<Services.Faces.FaceWorker>(_services!, new Services.Faces.FaceEngine(Path.Combine(_folder!.Root, "no-models")));
        await worker.GroupAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        var group = Assert.Single((await Services.Faces.PeopleService.ListAsync(db, anna.Id, new Services.Faces.PeopleCriteria(null, null, "count", true, 1, 50))).Items).Id;

        // the stranger is on the photo as "who is this?" and gets a name there: a new person
        var detail = await LibraryService.DetailAsync(db, anna.Id, photos[0]);
        var stranger = Assert.Single(detail!.People, p => p.Id is null).FaceId;
        Assert.True((await Services.Faces.PeopleService.AssignAsync(db, ben.Id, [stranger], "Oma Erna")).NotAllowed);
        Assert.False((await Services.Faces.PeopleService.AssignAsync(db, anna.Id, [stranger], "  ")).Ok);
        var erna = (await Services.Faces.PeopleService.AssignAsync(db, anna.Id, [stranger], "Oma Erna")).Value;
        db.ChangeTracker.Clear();
        Assert.Equal("Oma Erna", (await Services.Faces.PeopleService.GetAsync(db, anna.Id, erna))!.Name);
        Assert.True((await db.Faces.SingleAsync(f => f.Id == stranger)).IsConfirmed);

        // the group is split: one figure to a new person, one to Oma Erna's name (written otherwise), the grouping leaves them there
        var faces = await db.Faces.Where(f => f.PersonId == group).OrderBy(f => f.Id).Select(f => f.Id).ToListAsync();
        var elvis = (await Services.Faces.PeopleService.AssignAsync(db, anna.Id, [faces[0]], "Elvis")).Value;
        Assert.Equal(erna, (await Services.Faces.PeopleService.AssignAsync(db, anna.Id, [faces[1]], "oma erna")).Value);
        await worker.GroupAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal(elvis, (await db.Faces.SingleAsync(f => f.Id == faces[0])).PersonId);
        Assert.Equal(2, await db.Faces.CountAsync(f => f.PersonId == erna));
        Assert.Equal(1, await db.Faces.CountAsync(f => f.PersonId == group));
        Assert.True(await db.FaceRejections.AnyAsync(r => r.FaceId == faces[0] && r.PersonId == group));

        // the last face of the group goes too: the group is gone; "nobody" hides a face
        await Services.Faces.PeopleService.AssignAsync(db, anna.Id, [faces[2]], "Elvis");
        db.ChangeTracker.Clear();
        Assert.Null(await Services.Faces.PeopleService.GetAsync(db, anna.Id, group));
        Assert.False(await Services.Faces.PeopleService.IgnoreAsync(db, ben.Id, faces[2]));
        Assert.True(await Services.Faces.PeopleService.IgnoreAsync(db, anna.Id, faces[2]));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Faces.CountAsync(f => f.PersonId == elvis));
        Assert.DoesNotContain((await LibraryService.DetailAsync(db, anna.Id, photos[2]))!.People, p => p.FaceId == faces[2]);
    }
}

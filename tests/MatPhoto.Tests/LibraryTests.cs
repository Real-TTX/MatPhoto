using ImageMagick;
using MatPhoto.Data;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatPhoto.Tests;

/// <summary>The pieces of the library that need no database: names of albums, dates in names, places, scopes, the suggestion of the import, the picture hash.</summary>
public sealed class LibraryTests
{
    [Theory]
    [InlineData("2023.11.09 Hochzeit Julius", 2023, 11, 9, "Hochzeit Julius")]
    [InlineData("2023-11-09 Hochzeit", 2023, 11, 9, "Hochzeit")]
    [InlineData("20231109_Hochzeit", 2023, 11, 9, "Hochzeit")]
    [InlineData("2023.11.09-12 Gardasee", 2023, 11, 9, "Gardasee")]
    [InlineData("2023.11.09", 2023, 11, 9, "")]
    public void The_date_and_the_title_of_an_album_folder_are_read(string name, int year, int month, int day, string title)
    {
        var (date, parsed) = AlbumNames.Parse(name);
        Assert.Equal(new DateOnly(year, month, day), date);
        Assert.Equal(title, parsed);
    }

    [Theory]
    [InlineData("Urlaub")]
    [InlineData("2023 Rückblick")]
    [InlineData("2023.02.30 Kein Tag")]
    public void A_name_without_a_date_is_all_title(string name)
    {
        var (date, title) = AlbumNames.Parse(name);
        Assert.Null(date);
        Assert.Equal(name, title);
    }

    [Fact]
    public void A_new_album_is_named_in_the_pattern_and_cleaned()
    {
        Assert.Equal("2023.11.09 Hochzeit Julius", AlbumNames.Compose(new DateOnly(2023, 11, 9), "Hochzeit Julius", "yyyy.MM.dd"));
        Assert.Equal("2023-11-09 A B", AlbumNames.Compose(new DateOnly(2023, 11, 9), "A/B", "yyyy-MM-dd"));
        Assert.Equal("Ohne Datum", AlbumNames.Compose(null, " Ohne  Datum. ", "yyyy.MM.dd"));
        Assert.Equal("a b", AlbumNames.Clean("a:b"));
        Assert.Equal("2023/x", AlbumNames.ParentOf("2023/x/IMG.jpg"));
        Assert.Equal("x", AlbumNames.FolderName("2023/x"));
    }

    [Theory]
    [InlineData("IMG_20231109_142233", 2023, 11, 9, 14, 22, 33)]
    [InlineData("PXL_20231109_142233123", 2023, 11, 9, 14, 22, 33)]
    [InlineData("Screenshot_20231109-142233", 2023, 11, 9, 14, 22, 33)]
    [InlineData("2023-11-09 14.22.33", 2023, 11, 9, 14, 22, 33)]
    [InlineData("IMG-20231109-WA0001", 2023, 11, 9, 0, 0, 0)]
    public void The_date_in_a_file_name_is_read(string name, int y, int mo, int d, int h, int mi, int s)
        => Assert.Equal(new DateTime(y, mo, d, h, mi, s), MetadataReader.DateFromName(name));

    [Theory]
    [InlineData("DSC01234")]
    [InlineData("IMG_1234")]
    [InlineData("20231399_x")]
    public void A_name_without_a_date_has_none(string name) => Assert.Null(MetadataReader.DateFromName(name));

    [Theory]
    [InlineData("Screenshot_20231109-142233.png", true)]
    [InlineData("Bildschirmfoto 2023-11-09 um 14.22.33.png", true)]
    [InlineData("IMG_1234.PNG", false)]
    public void Screenshots_are_known_by_their_name(string name, bool screenshot) => Assert.Equal(screenshot, MetadataReader.IsScreenshotName(name));

    [Fact]
    public void A_position_of_a_video_is_read() => Assert.Equal((52.52, 13.405), MetadataReader.ParseIso6709("+52.5200+013.4050+034.000/"));

    [Fact]
    public void The_nearest_town_is_found_offline()
    {
        var hamburg = GeoPlaces.Nearest(53.5511, 9.9937);
        Assert.NotNull(hamburg);
        Assert.Equal("DE", hamburg.CountryCode);
        Assert.Contains("Hamburg", hamburg.City, StringComparison.Ordinal);
        Assert.Null(GeoPlaces.Nearest(0, -30)); // the Atlantic
        Assert.Null(GeoPlaces.Nearest(0, 0));
    }

    [Theory]
    [InlineData("album:12", "album", 12L, null)]
    [InlineData("list:4", "list", 4L, null)]
    [InlineData("place:DE/Berlin", "place", null, "DE/Berlin")]
    [InlineData("search:hochzeit 2023", "search", null, "hochzeit 2023")]
    [InlineData("videos", "videos", null, null)]
    [InlineData("album:x", "all", null, null)]
    [InlineData("drop table", "all", null, null)]
    public void A_scope_is_read_from_its_text(string text, string kind, long? id, string? value)
    {
        var scope = MediaScope.Parse(text);
        Assert.Equal((kind, id, value), (scope.Kind, scope.Id, scope.Value));
    }

    [Fact]
    public void Photos_and_folders_are_recognised()
    {
        Assert.True(MediaFormats.IsMedia("IMG_1.HEIC"));
        Assert.True(MediaFormats.IsMedia("clip.MOV"));
        Assert.False(MediaFormats.IsMedia("._IMG_1.jpg"));
        Assert.False(MediaFormats.IsMedia("notes.txt"));
        Assert.False(MediaFormats.IsScanned("@eaDir"));
        Assert.False(MediaFormats.IsScanned(MediaFormats.TrashFolder));
        Assert.False(MediaFormats.IsScanned("Backup", MediaFormats.ParseExcluded("Backup; tmp")));
        Assert.True(MediaFormats.IsScanned("2023"));
    }

    [Fact]
    public void The_import_suggests_albums_by_time_place_and_type()
    {
        var options = new LibraryOptions(null, ".cache", 480, 2048, 82, "yyyy.MM.dd", 8, 50, 6, 0);
        var hamburg = (53.5511, 9.9937);
        var munich = (48.1374, 11.5755);
        var items = new List<ImportItem>();
        void Add(DateTime? taken, (double, double)? where, MediaTraits traits = MediaTraits.None, string? city = null, string? folder = null)
            => items.Add(new ImportItem
            {
                Id = items.Count + 1, State = ImportItemState.Ready, TakenDate = taken, DateSource = taken is null ? DateSource.FileDate : DateSource.Exif,
                Latitude = where?.Item1, Longitude = where?.Item2, Traits = traits, City = city, SourceFolder = folder,
            });

        for (var i = 0; i < 5; i++)
        {
            Add(new DateTime(2023, 11, 9, 13, 0, 0).AddMinutes(i * 30), hamburg, city: "Hamburg");
        }

        for (var i = 0; i < 3; i++)
        {
            Add(new DateTime(2023, 11, 9, 20, 0, 0).AddMinutes(i * 10), munich, city: "Munich"); // the same day, far away: another album
        }

        Add(new DateTime(2023, 11, 20, 9, 0, 0), null, MediaTraits.Screenshot);
        Add(null, null);
        items.Add(new ImportItem { Id = 99, State = ImportItemState.Ready, TakenDate = new DateTime(2023, 11, 9), DuplicateOfMediaId = 7 });

        var groups = ImportService.Suggest(items, options, new DateOnly(2026, 1, 1));
        Assert.Equal(["2023.11.09 Hamburg", "2023.11.09 Munich", "2023.11.01 Screenshots", "2026.01.01 Import without date"],
            groups.Select(g => Languages(g.Group.Name)));
        Assert.Equal([5, 3, 1, 1], groups.Select(g => g.Items.Count));
        Assert.DoesNotContain(groups.SelectMany(g => g.Items), i => i.Id == 99);
        Assert.Equal("place", groups[0].Group.Reason);

        static string Languages(string name) => name.Replace("Bildschirmfotos", "Screenshots").Replace("Import ohne Datum", "Import without date");
    }

    [Fact]
    public void The_name_of_a_source_folder_names_its_album()
    {
        var options = new LibraryOptions(null, ".cache", 480, 2048, 82, "yyyy.MM.dd", 8, 50, 6, 0);
        var items = Enumerable.Range(0, 3).Select(i => new ImportItem
        {
            Id = i + 1, State = ImportItemState.Ready, TakenDate = new DateTime(2024, 7, 14, 10, i, 0), DateSource = DateSource.Exif, SourceFolder = "Fotos/Gardasee mit Oma",
        }).ToList();
        var group = Assert.Single(ImportService.Suggest(items, options, new DateOnly(2026, 1, 1)));
        Assert.Equal("2024.07.14 Gardasee mit Oma", group.Group.Name);
        Assert.Equal("folder", group.Group.Reason);
    }

    [Fact]
    public void A_folder_a_person_made_is_one_album_whatever_the_camera_clock_says()
    {
        var options = new LibraryOptions(null, ".cache", 480, 2048, 82, "yyyy.MM.dd", 8, 50, 6, 0);
        var items = new List<ImportItem>();
        void Add(string folder, DateTime? taken, string? name = null)
            => items.Add(new ImportItem
            {
                Id = items.Count + 1, State = ImportItemState.Ready, TakenDate = taken, DateSource = taken is null ? DateSource.FileDate : DateSource.Exif,
                SourceFolder = folder, FileName = name ?? $"IMG_{items.Count + 1:0000}.jpg",
            });

        // two folders of a camera that was never set: the same night, taken in turns
        var night = new DateTime(2006, 1, 2, 0, 1, 0);
        for (var i = 0; i < 6; i++)
        {
            Add(i % 2 == 0 ? "2008 Familie Olleck" : "2008 Klassenkameraden", night.AddMinutes(i * 7));
        }

        // a holiday in day folders, a festival with one video a month later, a photo without a date
        Add("2010 Urlaub Berlin/20100127", new DateTime(2010, 1, 27, 23, 0, 0));
        Add("2010 Urlaub Berlin/20100131", new DateTime(2010, 1, 31, 12, 0, 0));
        Add("2007 Wacken", new DateTime(2007, 7, 5, 21, 0, 0));
        Add("2007 Wacken", new DateTime(2007, 8, 4, 21, 0, 0));
        Add("2009 Weihnachten", new DateTime(2009, 12, 24, 13, 0, 0));
        Add("2009 Weihnachten", null, "Bild 015.jpg");

        // the folders of a phone name no album: they go by time
        Add("DCIM/100APPLE", new DateTime(2024, 5, 1, 10, 0, 0));
        Add("DCIM/100APPLE", new DateTime(2024, 5, 9, 10, 0, 0));

        var groups = ImportService.Suggest(items, options, new DateOnly(2026, 1, 1));
        Assert.Equal(
            ["2008 Familie Olleck", "2008 Klassenkameraden", "2007.07.05 Wacken", "2009.12.24 Weihnachten", "2010.01.27 Urlaub Berlin", "2024.05.01", "2024.05.09"],
            groups.Select(g => g.Group.Name));
        Assert.Equal([3, 3, 2, 2, 2, 1, 1], groups.Select(g => g.Items.Count));
        Assert.Equal(["folder", "folder", "folder", "folder", "folder", "time", "time"], groups.Select(g => g.Group.Reason));
    }

    [Fact]
    public void The_places_of_a_group_read_as_towns_and_their_country()
    {
        (string?, string?) P(string? city, string? country) => (city, country);
        Assert.Null(AlbumText.Places([P(null, null), P(null, null)]));
        Assert.Equal("Berlin, Potsdam · Germany", AlbumText.Places([P("Potsdam", "Germany"), P("Berlin", "Germany"), P("Berlin", "Germany"), P(null, null)]));
        Assert.Equal("Munich, Salzburg", AlbumText.Places([P("Munich", "Germany"), P("Munich", "Germany"), P("Salzburg", "Austria")]));
        Assert.Equal("A, B, C +1 · Italy", AlbumText.Places([P("A", "Italy"), P("B", "Italy"), P("C", "Italy"), P("D", "Italy")]));
        Assert.Equal("Germany", AlbumText.Places([P(null, "Germany")]));
    }

    [Theory]
    [InlineData("2010 Urlaub Berlin/20100127", "2010 Urlaub Berlin")]
    [InlineData("Reise/2010-01-27", "Reise")]
    [InlineData("Fotos/Gardasee mit Oma", "Fotos/Gardasee mit Oma")]
    [InlineData("DCIM/100CANON", null)]
    [InlineData("WhatsApp Images", null)]
    [InlineData("Fotos/2010", null)]
    [InlineData("20100127", null)]
    [InlineData(null, null)]
    public void The_album_folder_of_an_import_leaves_out_days_and_the_folders_of_devices(string? source, string? expected)
        => Assert.Equal(expected, AlbumNames.AlbumFolderOf(source));

    [Fact]
    public void Similar_pictures_have_close_hashes_and_different_ones_do_not()
    {
        using var a = new MagickImage("plasma:#ff8040-#2040a0", 400, 300);
        using var b = a.Clone();
        b.Resize(200, 150);
        b.Quality = 40;
        using var c = new MagickImage("plasma:#20a040-#a02080", 400, 300);
        var ha = ImageRenderer.DifferenceHash(a);
        var hb = ImageRenderer.DifferenceHash(b);
        var hc = ImageRenderer.DifferenceHash(c);
        Assert.True(ImageRenderer.Distance(ha, hb) <= 6, $"{ImageRenderer.Distance(ha, hb)}");
        Assert.True(ImageRenderer.Distance(ha, hc) > 12, $"{ImageRenderer.Distance(ha, hc)}");
    }

    [Fact]
    public async Task A_photo_is_read_with_its_date_camera_place_and_thumbnails()
    {
        using var folder = new TempFolder();
        var file = Path.Combine(folder.Root, "IMG_0001.jpg");
        using (var image = new MagickImage("plasma:#ff8040-#2040a0", 1200, 900))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Apple");
            exif.SetValue(ExifTag.Model, "iPhone 15 Pro");
            exif.SetValue(ExifTag.DateTimeOriginal, "2023:11:09 14:22:33");
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(53, 1), new Rational(33, 1), new Rational(4, 1)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(9, 1), new Rational(59, 1), new Rational(37, 1)]);
            image.SetProfile(exif);
            image.Orientation = Orientation.RightTop;
            image.Write(file);
        }

        var data = Path.Combine(folder.Root, "data");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = data, ["StorageRoot"] = folder.Root }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddDataProtection().Services.AddHttpClient().BuildServiceProvider();
        var settings = new LibrarySettings(services.GetRequiredService<IServiceScopeFactory>());
        settings.Reload(new SystemSetting());
        var connections = new StorageConnections(services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), services.GetRequiredService<IHttpClientFactory>(), configuration);
        var cache = new ThumbnailCache(settings, connections, services.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<ThumbnailCache>.Instance);
        var indexer = new MediaIndexer(cache, new VideoTools(NullLogger<VideoTools>.Instance), settings, configuration, NullLogger<MediaIndexer>.Instance);

        var provider = new LocalProvider(folder.Root, data);
        var facts = await indexer.IndexAsync(provider, "IMG_0001.jpg", new FileInfo(file).Length, File.GetLastWriteTimeUtc(file), null, CancellationToken.None);

        Assert.Equal(new DateTime(2023, 11, 9, 14, 22, 33), facts.TakenDate);
        Assert.Equal(DateSource.Exif, facts.DateSource);
        Assert.Equal("iPhone 15 Pro", facts.CameraModel);
        Assert.Equal((900, 1200), (facts.Width, facts.Height)); // turned by the orientation of the camera
        Assert.Equal("DE", facts.Place?.CountryCode);
        Assert.True(facts.ThumbReady);
        Assert.Equal(64, facts.ContentHash.Length);
        var thumb = await cache.ReadAsync(facts.ContentHash, ThumbKind.Thumb, CancellationToken.None);
        Assert.NotNull(thumb);
        using var tile = new MagickImage(thumb);
        Assert.Equal(480u, tile.Height);
        Assert.Equal(360u, tile.Width);
    }
}

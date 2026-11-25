using System.Security.Cryptography;
using ImageMagick;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;

namespace MatPhoto.Services.Library;

/// <summary>Everything MatPhoto learned from one file: what it is, when and where it was taken, with which camera, and whether its thumbnails are in the cache.</summary>
public sealed class MediaFacts
{
    public MediaType Type { get; init; }

    public MediaTraits Traits { get; set; }

    public string MimeType { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTime FileModified { get; init; }

    public string ContentHash { get; set; } = string.Empty;

    public long? PerceptualHash { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTime? TakenDate { get; set; }

    public DateSource DateSource { get; set; }

    public DateTime SortDate => TakenDate ?? DateTime.SpecifyKind(MetadataReader.ToLocal(FileModified), DateTimeKind.Unspecified);

    public string? CameraMake { get; set; }

    public string? CameraModel { get; set; }

    public string? LensModel { get; set; }

    public double? FNumber { get; set; }

    public double? ExposureTime { get; set; }

    public int? Iso { get; set; }

    public double? FocalLength { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? Altitude { get; set; }

    public Place? Place { get; set; }

    public int? DurationMs { get; set; }

    public string? VideoCodec { get; set; }

    public bool ThumbReady { get; set; }

    public string? ThumbError { get; set; }

    /// <summary>Copies the facts into a record of the library.</summary>
    public void ApplyTo(Media m)
    {
        m.Type = Type;
        m.Traits = (m.Traits & (MediaTraits.LivePhoto | MediaTraits.LiveVideo)) | Traits;
        m.MimeType = MimeType;
        m.Size = Size;
        m.FileModified = FileModified;

        // another picture (or its preview came again after it had failed): its faces are looked for again
        if (m.ContentHash != ContentHash || m.FaceState == FaceState.Failed)
        {
            m.FaceState = FaceState.Pending;
        }

        m.ContentHash = ContentHash;
        m.PerceptualHash = PerceptualHash;
        m.Width = Width;
        m.Height = Height;
        m.TakenDate = TakenDate;
        m.DateSource = DateSource;
        m.SortDate = SortDate;
        m.CameraMake = CameraMake;
        m.CameraModel = CameraModel;
        m.LensModel = LensModel;
        m.FNumber = FNumber;
        m.ExposureTime = ExposureTime;
        m.Iso = Iso;
        m.FocalLength = FocalLength;
        m.Latitude = Latitude;
        m.Longitude = Longitude;
        m.Altitude = Altitude;
        m.City = Place?.City;
        m.Region = Place?.Region;
        m.Country = Place?.Country;
        m.CountryCode = Place?.CountryCode;
        m.DurationMs = DurationMs;
        m.VideoCodec = VideoCodec;
        m.ThumbState = ThumbReady ? ThumbState.Ready : ThumbState.Failed;
        m.ThumbError = ThumbError is { Length: > 500 } e ? e[..500] : ThumbError;
    }
}

/// <summary>
/// Reads one photo or video of a storage once: its bytes are hashed (the key of the thumbnails and of the duplicates), its metadata read, its tile and preview made and
/// written into the cache. A video is read by ffmpeg from the folder where it lies (a local storage) or from a temporary copy; of a large remote video only the start
/// and the end are copied (the frame at one second and the index of the file are there). The scan of a library and the analysis of an import both use it.
/// </summary>
public sealed class MediaIndexer(ThumbnailCache cache, VideoTools video, LibrarySettings settings, IConfiguration configuration, ILogger<MediaIndexer> log)
{
    /// <summary>A photo larger than this is not read (a broken file or not a photo).</summary>
    public const long MaxPhotoBytes = 400L * 1024 * 1024;

    /// <summary>A video up to this size is hashed whole; a larger one by its size, start and end.</summary>
    private const long FullHashBytes = 64L * 1024 * 1024;

    private const int SampleBytes = 4 * 1024 * 1024;

    /// <summary>A remote video up to this size is copied whole for ffmpeg; of a larger one only the head and the tail.</summary>
    private const long FullCopyBytes = 256L * 1024 * 1024;

    private const int HeadBytes = 48 * 1024 * 1024;
    private const int TailBytes = 16 * 1024 * 1024;

    private readonly string _temp = Path.Combine(configuration["DataDirectory"] ?? "/data", "tmp");

    /// <summary>Indexes a file. <paramref name="folderDate"/> is the date of the album folder (used when the file has none of its own).</summary>
    public async Task<MediaFacts> IndexAsync(IStorageProvider provider, string path, long size, DateTime modifiedUtc, DateOnly? folderDate, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        var format = MediaFormats.Of(fileName) ?? throw new StorageException(StorageError.NotSupported);
        if (size <= 0)
        {
            throw new StorageException(StorageError.Worded, Text.T("Photos.Error.Empty"));
        }

        var facts = new MediaFacts { Type = format.Type, MimeType = format.MimeType, Size = size, FileModified = DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc) };
        MediaMetadata meta;
        if (format.Type == MediaType.Photo)
        {
            meta = await IndexPhotoAsync(provider, path, size, format, facts, ct);
        }
        else
        {
            meta = await IndexVideoAsync(provider, path, size, format, facts, ct);
        }

        Complete(facts, meta, format, fileName, folderDate);
        return facts;
    }

    /// <summary>Indexes a file of the data volume (an upload of an import).</summary>
    public async Task<MediaFacts> IndexLocalAsync(string file, string fileName, DateOnly? folderDate, CancellationToken ct)
    {
        var format = MediaFormats.Of(fileName) ?? throw new StorageException(StorageError.NotSupported);
        var info = new FileInfo(file);
        if (info.Exists && info.Length == 0)
        {
            throw new StorageException(StorageError.Worded, Text.T("Photos.Error.Empty"));
        }

        var provider = new LocalProvider(Path.GetDirectoryName(file)!, null);
        var facts = new MediaFacts { Type = format.Type, MimeType = format.MimeType, Size = info.Length, FileModified = info.LastWriteTimeUtc };
        var meta = format.Type == MediaType.Photo
            ? await IndexPhotoAsync(provider, Path.GetFileName(file), info.Length, format, facts, ct)
            : await IndexVideoAsync(provider, Path.GetFileName(file), info.Length, format, facts, ct);
        Complete(facts, meta, format, fileName, folderDate);
        return facts;
    }

    private async Task<MediaMetadata> IndexPhotoAsync(IStorageProvider provider, string path, long size, MediaFormat format, MediaFacts facts, CancellationToken ct)
    {
        if (size > MaxPhotoBytes)
        {
            throw new StorageException(StorageError.TooLarge);
        }

        byte[] bytes;
        await using (var stream = await provider.OpenReadAsync(path, 0, ct))
        {
            using var buffer = new MemoryStream(size is > 0 and < int.MaxValue ? (int)size : 0);
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        facts.ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        MediaMetadata meta;
        using (var memory = new MemoryStream(bytes, writable: false))
        {
            meta = MetadataReader.Read(memory, format);
        }

        var options = settings.Current;
        try
        {
            var rendered = await ImageRenderer.RenderAsync(bytes, Path.GetExtension(path), options.ThumbnailHeight, options.PreviewSize, options.ThumbnailQuality, ct, meta.Orientation);
            await cache.WriteAsync(facts.ContentHash, ThumbKind.Thumb, rendered.Thumb, ct);
            await cache.WriteAsync(facts.ContentHash, ThumbKind.Preview, rendered.Preview, ct);
            facts.Width = rendered.Width;
            facts.Height = rendered.Height;
            facts.PerceptualHash = rendered.PerceptualHash;
            facts.ThumbReady = true;
            if (rendered.Animated)
            {
                facts.Traits |= MediaTraits.Animated;
            }
        }
        catch (MagickException ex)
        {
            log.LogInformation("No thumbnail for {Path}: {Message}", path, ex.Message);
            facts.ThumbError = ex.Message;
            (facts.Width, facts.Height) = Shown(meta);
        }

        return meta;
    }

    private async Task<MediaMetadata> IndexVideoAsync(IStorageProvider provider, string path, long size, MediaFormat format, MediaFacts facts, CancellationToken ct)
    {
        var local = provider.LocalPathOf(path);
        string? temp = null;
        try
        {
            var file = local ?? (temp = await CopyForFfmpegAsync(provider, path, size, ct));
            facts.ContentHash = await HashVideoAsync(file, size, ct);

            MediaMetadata meta;
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                meta = MetadataReader.Read(stream, format);
            }

            if (await video.InfoAsync(file, ct) is { } info)
            {
                meta.Width = info.Width > 0 ? info.Width : meta.Width;
                meta.Height = info.Height > 0 ? info.Height : meta.Height;
                meta.DurationMs = info.DurationMs ?? meta.DurationMs;
                meta.VideoCodec = info.Codec;
                if (info.TakenDate is { } taken)
                {
                    meta.TakenDate = taken;
                    meta.DateSource = DateSource.Exif;
                }

                meta.Latitude ??= info.Latitude;
                meta.Longitude ??= info.Longitude;
                meta.CameraMake ??= info.Make;
                meta.CameraModel ??= info.Model;
            }

            facts.Width = meta.Width;
            facts.Height = meta.Height;
            var frame = await video.FrameAsync(file, meta.DurationMs, ct);
            if (frame is null)
            {
                facts.ThumbError = video.IsAvailable ? "ffmpeg could not read a frame of the video." : "ffmpeg is not installed.";
                return meta;
            }

            var options = settings.Current;
            try
            {
                var rendered = await ImageRenderer.RenderAsync(frame, ".jpg", options.ThumbnailHeight, options.PreviewSize, options.ThumbnailQuality, ct);
                await cache.WriteAsync(facts.ContentHash, ThumbKind.Thumb, rendered.Thumb, ct);
                await cache.WriteAsync(facts.ContentHash, ThumbKind.Preview, rendered.Preview, ct);
                facts.PerceptualHash = rendered.PerceptualHash;
                facts.ThumbReady = true;
                if (facts.Width == 0)
                {
                    facts.Width = rendered.Width;
                    facts.Height = rendered.Height;
                }
            }
            catch (MagickException ex)
            {
                facts.ThumbError = ex.Message;
            }

            return meta;
        }
        finally
        {
            if (temp is not null)
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The date, the camera, the place and the traits from what was read.</summary>
    private static void Complete(MediaFacts facts, MediaMetadata meta, MediaFormat format, string fileName, DateOnly? folderDate)
    {
        if (meta.TakenDate is { } taken)
        {
            facts.TakenDate = taken;
            facts.DateSource = meta.DateSource == DateSource.None ? DateSource.Exif : meta.DateSource;
        }
        else if (MetadataReader.DateFromName(Path.GetFileNameWithoutExtension(fileName)) is { } named)
        {
            facts.TakenDate = named;
            facts.DateSource = DateSource.FileName;
        }
        else if (folderDate is { } day)
        {
            facts.TakenDate = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
            facts.DateSource = DateSource.Folder;
        }
        else
        {
            facts.DateSource = DateSource.FileDate;
        }

        facts.CameraMake = meta.CameraMake;
        facts.CameraModel = meta.CameraModel;
        facts.LensModel = meta.LensModel;
        facts.FNumber = meta.FNumber;
        facts.ExposureTime = meta.ExposureTime;
        facts.Iso = meta.Iso;
        facts.FocalLength = meta.FocalLength;
        facts.Latitude = meta.Latitude;
        facts.Longitude = meta.Longitude;
        facts.Altitude = meta.Altitude;
        facts.DurationMs = meta.DurationMs;
        facts.VideoCodec ??= meta.VideoCodec;
        if (facts.Latitude is { } lat && facts.Longitude is { } lon)
        {
            facts.Place = GeoPlaces.Nearest(lat, lon);
        }

        if (facts.Width == 0 || facts.Height == 0)
        {
            (facts.Width, facts.Height) = Shown(meta);
        }

        if (format.IsRaw)
        {
            facts.Traits |= MediaTraits.Raw;
        }

        if (format.Type == MediaType.Photo)
        {
            if (meta.SaysScreenshot || MetadataReader.IsScreenshotName(fileName))
            {
                facts.Traits |= MediaTraits.Screenshot;
            }

            if (meta.LensModel is { } lens && lens.Contains("front", StringComparison.OrdinalIgnoreCase))
            {
                facts.Traits |= MediaTraits.Selfie;
            }

            if (facts.Width > 0 && facts.Height > 0 && Math.Max(facts.Width, facts.Height) >= 2.5 * Math.Min(facts.Width, facts.Height))
            {
                facts.Traits |= MediaTraits.Panorama;
            }
        }
        else if (MetadataReader.IsScreenshotName(fileName))
        {
            facts.Traits |= MediaTraits.Screenshot;
        }
    }

    /// <summary>The size as shown: turned by 90 degrees for the orientations 5 to 8.</summary>
    private static (int Width, int Height) Shown(MediaMetadata meta) => meta.Orientation is >= 5 and <= 8 ? (meta.Height, meta.Width) : (meta.Width, meta.Height);

    private static async Task<string> HashVideoAsync(string file, long size, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (size <= FullHashBytes)
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(size));
        var buffer = new byte[SampleBytes];
        var read = await stream.ReadAtLeastAsync(buffer, SampleBytes, throwOnEndOfStream: false, ct);
        hash.AppendData(buffer, 0, read);
        stream.Seek(Math.Max(0, size - SampleBytes), SeekOrigin.Begin);
        read = await stream.ReadAtLeastAsync(buffer, SampleBytes, throwOnEndOfStream: false, ct);
        hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// A temporary copy of a remote video for ffmpeg: whole up to 256 MB; of a larger one the first 48 MB and the last 16 MB at their places in a file of the full
    /// length (the gap reads as zeros), which is where the frame at one second, the index of an MP4 and the start and end the hash reads lie.
    /// </summary>
    private async Task<string> CopyForFfmpegAsync(IStorageProvider provider, string path, long size, CancellationToken ct)
    {
        Directory.CreateDirectory(_temp);
        var target = Path.Combine(_temp, $"{Guid.NewGuid():n}{Path.GetExtension(path).ToLowerInvariant()}");
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        if (size <= FullCopyBytes)
        {
            await using var input = await provider.OpenReadAsync(path, 0, ct);
            await input.CopyToAsync(output, ct);
            return target;
        }

        output.SetLength(size);
        await using (var head = await provider.OpenReadAsync(path, 0, ct))
        {
            await CopyAsync(head, output, HeadBytes, ct);
        }

        output.Seek(size - TailBytes, SeekOrigin.Begin);
        await using (var tail = await provider.OpenReadAsync(path, size - TailBytes, ct))
        {
            await CopyAsync(tail, output, TailBytes, ct);
        }

        return target;
    }

    private static async Task CopyAsync(Stream input, Stream output, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }
    }
}

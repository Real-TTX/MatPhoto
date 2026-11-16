using System.Globalization;
using System.Text.RegularExpressions;
using MatPhoto.Data;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.Xmp;

namespace MatPhoto.Services.Library;

/// <summary>What the metadata of a file says (all optional: a screenshot has no camera, a scan no date).</summary>
public sealed class MediaMetadata
{
    public DateTime? TakenDate { get; set; }

    public DateSource DateSource { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>The EXIF orientation (1 = as stored; 5 to 8 = turned by 90 degrees).</summary>
    public int Orientation { get; set; } = 1;

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

    public int? DurationMs { get; set; }

    public string? VideoCodec { get; set; }

    /// <summary>The software or a comment says it is a screenshot (iOS writes "Screenshot" into the XMP of the PNG).</summary>
    public bool SaysScreenshot { get; set; }

    public bool HasCamera => !string.IsNullOrWhiteSpace(CameraModel) || !string.IsNullOrWhiteSpace(CameraMake);
}

/// <summary>
/// Reads the metadata of photos and videos (EXIF, XMP, GPS, QuickTime) with MetadataExtractor, and the date from a file or folder name when the file has none
/// (<c>IMG_20231109_142233.jpg</c>, <c>Screenshot 2023-11-09 at 14.22.33.png</c>, <c>2023.11.09 Hochzeit Julius</c>). Nothing here throws for a broken file: what
/// cannot be read stays empty.
/// </summary>
public static partial class MetadataReader
{
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d{2})[-_.]?(0[1-9]|1[0-2])[-_.]?(0[1-9]|[12]\d|3[01])(?:[ _T-]?(?:at |um )?([01]\d|2[0-3])[-_.:h]?([0-5]\d)[-_.:m]?([0-5]\d)(?:\d{1,3})?)?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DateInNameRegex();

    [GeneratedRegex(@"^(screenshot|screen shot|bildschirmfoto|capture d.écran|schermafbeelding|captura de pantalla|scr_|screencapture)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotNameRegex();

    [GeneratedRegex(@"([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)?")]
    private static partial Regex Iso6709Regex();

    /// <summary>Reads what the file says. The stream is read from its start; it is not closed.</summary>
    public static MediaMetadata Read(Stream stream, MediaFormat format)
    {
        var result = new MediaMetadata();
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(stream);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException)
        {
            return result;
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

        if (ifd0 is not null)
        {
            result.CameraMake = Clean(ifd0.GetDescription(ExifDirectoryBase.TagMake));
            result.CameraModel = Clean(ifd0.GetDescription(ExifDirectoryBase.TagModel));
            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation) && orientation is >= 1 and <= 8)
            {
                result.Orientation = orientation;
            }

            var software = ifd0.GetDescription(ExifDirectoryBase.TagSoftware);
            if (software is not null && software.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            {
                result.SaysScreenshot = true;
            }
        }

        if (sub is not null)
        {
            if (TryDate(sub, ExifDirectoryBase.TagDateTimeOriginal, out var taken) || TryDate(sub, ExifDirectoryBase.TagDateTimeDigitized, out taken))
            {
                result.TakenDate = taken;
                result.DateSource = DateSource.Exif;
            }

            result.LensModel = Clean(sub.GetDescription(ExifDirectoryBase.TagLensModel));
            if (sub.TryGetRational(ExifDirectoryBase.TagFNumber, out var f) && f.Denominator != 0)
            {
                result.FNumber = Math.Round(f.ToDouble(), 1);
            }

            if (sub.TryGetRational(ExifDirectoryBase.TagExposureTime, out var exposure) && exposure.Denominator != 0)
            {
                result.ExposureTime = exposure.ToDouble();
            }

            if (sub.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso) && iso > 0)
            {
                result.Iso = iso;
            }

            if (sub.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focal) && focal.Denominator != 0)
            {
                result.FocalLength = Math.Round(focal.ToDouble(), 1);
            }

            if (sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) && w > 0 && h > 0)
            {
                result.Width = w;
                result.Height = h;
            }

            var comment = sub.GetDescription(ExifDirectoryBase.TagUserComment);
            if (comment is not null && comment.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            {
                result.SaysScreenshot = true;
            }
        }

        if (result.TakenDate is null && ifd0 is not null && TryDate(ifd0, ExifDirectoryBase.TagDateTime, out var changed))
        {
            result.TakenDate = changed;
            result.DateSource = DateSource.Exif;
        }

        if (gps is not null && gps.TryGetGeoLocation(out var location) && !location.IsZero)
        {
            result.Latitude = Math.Round(location.Latitude, 6);
            result.Longitude = Math.Round(location.Longitude, 6);
            if (gps.TryGetRational(GpsDirectory.TagAltitude, out var altitude) && altitude.Denominator != 0)
            {
                var below = gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var reference) && reference == 1;
                result.Altitude = Math.Round(below ? -altitude.ToDouble() : altitude.ToDouble(), 1);
            }
        }

        // the size of the picture where EXIF does not have it
        if (result.Width == 0)
        {
            if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg && jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh))
            {
                result.Width = jw;
                result.Height = jh;
            }
            else if (directories.OfType<PngDirectory>().FirstOrDefault(d => d.Name == "PNG-IHDR") is { } png && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph))
            {
                result.Width = pw;
                result.Height = ph;
            }
        }

        foreach (var xmp in directories.OfType<XmpDirectory>())
        {
            if (xmp.GetXmpProperties().Values.Any(v => v.Contains("screenshot", StringComparison.OrdinalIgnoreCase)))
            {
                result.SaysScreenshot = true;
            }
        }

        if (format.Type == MediaType.Video)
        {
            ReadQuickTime(directories, result);
        }

        return result;
    }

    /// <summary>The QuickTime / MP4 boxes: when it was recorded (Apple's local date first, else the UTC date of the movie header), how long, where.</summary>
    private static void ReadQuickTime(IReadOnlyList<MetadataExtractor.Directory> directories, MediaMetadata result)
    {
        if (directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault() is { } movie)
        {
            if (result.TakenDate is null && movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year > 1971)
            {
                result.TakenDate = ToLocal(DateTime.SpecifyKind(created, DateTimeKind.Utc));
                result.DateSource = DateSource.Exif;
            }

            if (movie.GetObject(QuickTimeMovieHeaderDirectory.TagDuration) is TimeSpan duration && duration > TimeSpan.Zero)
            {
                result.DurationMs = (int)Math.Min(int.MaxValue, duration.TotalMilliseconds);
            }
        }

        foreach (var directory in directories)
        {
            foreach (var tag in directory.Tags)
            {
                var name = tag.Name;
                var value = tag.Description;
                if (value is null)
                {
                    continue;
                }

                if (name.Contains("creationdate", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    result.TakenDate = local.DateTime;
                    result.DateSource = DateSource.Exif;
                }
                else if ((name.Contains("ISO6709", StringComparison.OrdinalIgnoreCase) || name.Contains("GPS", StringComparison.OrdinalIgnoreCase)) && ParseIso6709(value) is { } position)
                {
                    result.Latitude ??= position.Latitude;
                    result.Longitude ??= position.Longitude;
                }
                else if (name.Equals("Make", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".make", StringComparison.OrdinalIgnoreCase))
                {
                    result.CameraMake ??= Clean(value);
                }
                else if (name.Equals("Model", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                {
                    result.CameraModel ??= Clean(value);
                }
            }
        }

        if (result.Width == 0 && directories.OfType<QuickTimeTrackHeaderDirectory>().FirstOrDefault(t => t.TryGetInt32(QuickTimeTrackHeaderDirectory.TagWidth, out var tw) && tw > 0) is { } track
            && track.TryGetInt32(QuickTimeTrackHeaderDirectory.TagWidth, out var width) && track.TryGetInt32(QuickTimeTrackHeaderDirectory.TagHeight, out var height))
        {
            result.Width = width;
            result.Height = height;
        }
    }

    /// <summary>A position of ISO 6709 (<c>+52.5200+013.4050+034.000/</c>): the format of the GPS of videos.</summary>
    public static (double Latitude, double Longitude)? ParseIso6709(string? value)
    {
        var match = Iso6709Regex().Match(value ?? string.Empty);
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180 || (lat == 0 && lon == 0))
        {
            return null;
        }

        return (Math.Round(lat, 6), Math.Round(lon, 6));
    }

    /// <summary>The date in a name: <c>IMG_20231109_142233</c>, <c>PXL_20231109_142233123</c>, <c>2023-11-09 14.22.33</c>, <c>IMG-20231109-WA0001</c> (only the day).</summary>
    public static DateTime? DateFromName(string name)
    {
        var match = DateInNameRegex().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 1900 || year > DateTime.Today.Year + 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var hour = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var minute = match.Groups[5].Success ? int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
        var second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>The name says it is a screenshot (<c>Screenshot_20231109-142233.png</c>, <c>Bildschirmfoto 2023-11-09 um 14.22.33.png</c>).</summary>
    public static bool IsScreenshotName(string fileName) => ScreenshotNameRegex().IsMatch(fileName);

    /// <summary>A UTC time in the time zone of the server (TZ of the container): a video stores UTC, the timeline shows the local time.</summary>
    public static DateTime ToLocal(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    }

    private static bool TryDate(MetadataExtractor.Directory directory, int tag, out DateTime value)
    {
        value = default;
        if (!directory.TryGetDateTime(tag, out var date) || date.Year < 1900 || date.Year > DateTime.Today.Year + 1)
        {
            return false;
        }

        value = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return true;
    }

    private static string? Clean(string? value)
    {
        var text = value?.Trim().Trim('\0').Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > 100 ? text[..100] : text;
    }
}

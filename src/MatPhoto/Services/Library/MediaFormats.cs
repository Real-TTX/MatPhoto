using MatPhoto.Data;

namespace MatPhoto.Services.Library;

/// <summary>A file type MatPhoto shows: photo or video, its MIME type, whether a browser can show the original itself, whether it is a camera RAW.</summary>
public sealed record MediaFormat(MediaType Type, string MimeType, bool BrowserCanShow, bool IsRaw = false);

/// <summary>
/// Which files are photos and videos (by the extension), and which folders a scan never enters (the folders a NAS keeps for itself, the trash and the cache of MatPhoto).
/// </summary>
public static class MediaFormats
{
    /// <summary>The folder MatPhoto moves removed photos into (in the same storage, so nothing is lost by a click).</summary>
    public const string TrashFolder = ".matphoto-trash";

    private static readonly Dictionary<string, MediaFormat> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = new(MediaType.Photo, "image/jpeg", true),
        [".jpeg"] = new(MediaType.Photo, "image/jpeg", true),
        [".jpe"] = new(MediaType.Photo, "image/jpeg", true),
        [".png"] = new(MediaType.Photo, "image/png", true),
        [".gif"] = new(MediaType.Photo, "image/gif", true),
        [".webp"] = new(MediaType.Photo, "image/webp", true),
        [".avif"] = new(MediaType.Photo, "image/avif", true),
        [".bmp"] = new(MediaType.Photo, "image/bmp", true),
        [".heic"] = new(MediaType.Photo, "image/heic", false),
        [".heif"] = new(MediaType.Photo, "image/heif", false),
        [".tif"] = new(MediaType.Photo, "image/tiff", false),
        [".tiff"] = new(MediaType.Photo, "image/tiff", false),
        [".jxl"] = new(MediaType.Photo, "image/jxl", false),
        [".dng"] = new(MediaType.Photo, "image/x-adobe-dng", false, true),
        [".cr2"] = new(MediaType.Photo, "image/x-canon-cr2", false, true),
        [".cr3"] = new(MediaType.Photo, "image/x-canon-cr3", false, true),
        [".nef"] = new(MediaType.Photo, "image/x-nikon-nef", false, true),
        [".arw"] = new(MediaType.Photo, "image/x-sony-arw", false, true),
        [".orf"] = new(MediaType.Photo, "image/x-olympus-orf", false, true),
        [".rw2"] = new(MediaType.Photo, "image/x-panasonic-rw2", false, true),
        [".raf"] = new(MediaType.Photo, "image/x-fuji-raf", false, true),
        [".pef"] = new(MediaType.Photo, "image/x-pentax-pef", false, true),
        [".srw"] = new(MediaType.Photo, "image/x-samsung-srw", false, true),
        [".mp4"] = new(MediaType.Video, "video/mp4", true),
        [".m4v"] = new(MediaType.Video, "video/mp4", true),
        [".mov"] = new(MediaType.Video, "video/quicktime", true),
        [".webm"] = new(MediaType.Video, "video/webm", true),
        [".mkv"] = new(MediaType.Video, "video/x-matroska", false),
        [".avi"] = new(MediaType.Video, "video/x-msvideo", false),
        [".mts"] = new(MediaType.Video, "video/mp2t", false),
        [".m2ts"] = new(MediaType.Video, "video/mp2t", false),
        [".3gp"] = new(MediaType.Video, "video/3gpp", true),
        [".wmv"] = new(MediaType.Video, "video/x-ms-wmv", false),
    };

    /// <summary>Folders a scan never enters: the NAS's own (Synology, QNAP), the recycle bins, the systems', MatPhoto's trash and cache.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", "#snapshot", "@Recycle", "@Recently-Snapshot", ".@__thumb", "@__thumb", ".Trash", ".Trashes", ".Spotlight-V100", ".fseventsd",
        "$RECYCLE.BIN", "System Volume Information", ".thumbnails", ".picasaoriginals", ".AppleDouble", "lost+found", TrashFolder, ".matphoto-cache",
    };

    public static MediaFormat? Of(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && Known.TryGetValue(extension, out var format) ? format : null;
    }

    public static bool IsMedia(string fileName) => Of(fileName) is not null && !IsHiddenFile(fileName);

    /// <summary>Files a system writes next to the photos (<c>._IMG_0001.JPG</c> of a Mac, <c>.DS_Store</c>) are not photos.</summary>
    public static bool IsHiddenFile(string fileName) => fileName.StartsWith("._", StringComparison.Ordinal) || fileName.StartsWith('.');

    /// <summary>Whether a scan enters a folder: never the built-in ones, never one of <paramref name="extra"/> (the setting of the storage, separated by ;).</summary>
    public static bool IsScanned(string folderName, IReadOnlySet<string>? extra = null)
        => !SkippedFolders.Contains(folderName) && !folderName.StartsWith('.') && (extra is null || !extra.Contains(folderName));

    /// <summary>The folders of the setting of a storage (<c>Backup; tmp</c>).</summary>
    public static IReadOnlySet<string> ParseExcluded(string? setting)
        => (setting ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The extensions of the file picker of the import (<c>accept</c>).</summary>
    public static string AcceptList => string.Join(",", Known.Keys.Order(StringComparer.Ordinal)) + ",image/*,video/*";
}

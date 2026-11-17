using System.Globalization;
using System.Text.RegularExpressions;

namespace MatPhoto.Services.Library;

/// <summary>
/// The names of album folders, which people read on the NAS too: <c>2023.11.09 Hochzeit Julius</c>. <see cref="Parse"/> reads the date and the title of a name (also
/// <c>2023-11-09</c>, <c>2023_11_09</c>, <c>20231109</c> and a range <c>2023.11.09-12</c>), <see cref="Compose"/> writes a name in the pattern of the installation, and
/// <see cref="Clean"/> makes a name every file system takes.
/// </summary>
public static partial class AlbumNames
{
    [GeneratedRegex(@"^((?:19|20)\d{2})[.\-_ ]?(0[1-9]|1[0-2])[.\-_ ]?(0[1-9]|[12]\d|3[01])(?:\s*[-–]\s*\d{1,2}(?:[.\-_]\d{1,2}){0,2})?(?=$|[\s_\-–])[\s_\-–]*(.*)$")]
    private static partial Regex DatedRegex();

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]")]
    private static partial Regex ForbiddenRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();

    /// <summary>The date at the start of a folder name and the rest (the title); no date: the whole name is the title.</summary>
    public static (DateOnly? Date, string Title) Parse(string name)
    {
        var match = DatedRegex().Match(name.Trim());
        if (!match.Success)
        {
            return (null, name.Trim());
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (day > DateTime.DaysInMonth(year, month))
        {
            return (null, name.Trim());
        }

        return (new DateOnly(year, month, day), match.Groups[4].Value.Trim());
    }

    /// <summary>A folder name in the pattern of the installation (<c>yyyy.MM.dd</c> + title); without a date only the title.</summary>
    public static string Compose(DateOnly? date, string? title, string dateFormat)
    {
        var text = Clean(title ?? string.Empty);
        if (date is not { } d)
        {
            return text;
        }

        string prefix;
        try
        {
            prefix = d.ToString(dateFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            prefix = d.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        }

        return text.Length == 0 ? prefix : $"{prefix} {text}";
    }

    /// <summary>A name that every file system takes: no slashes, colons or other forbidden characters, no dot or space at the end, at most 120 characters.</summary>
    public static string Clean(string name)
    {
        var text = SpacesRegex().Replace(ForbiddenRegex().Replace(name, " "), " ").Trim().TrimEnd('.', ' ');
        if (text.Length > 120)
        {
            text = text[..120].TrimEnd('.', ' ');
        }

        return text is "." or ".." ? string.Empty : text;
    }

    [GeneratedRegex(@"^((?:19|20)\d{2})[\s_\-–]+(\S.*)$")]
    private static partial Regex YearRegex();

    /// <summary>Folders a camera, a phone or a messenger makes, and folders that only sort (<c>Fotos</c>, <c>2010</c>): they name no album.</summary>
    [GeneratedRegex(@"^(dcim|camera|kamera|camera roll|aufnahmen|pictures|bilder|fotos|photos|images|img|videos|movies|screenshots|bildschirmfotos|download|downloads|whatsapp|whatsapp images|whatsapp video|whatsapp animated gifs|telegram|telegram images|signal|sent|private|inbox|eingang|import|imports|upload|uploads|neu|new|misc|sonstiges|temp|tmp|\d{3}[a-z_]{5}|\d{3}_\d{4}|\d{3}media|\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericRegex();

    /// <summary>A year at the start of a folder name and the rest (<c>2010 Ausflug Hamburg</c> → 2010, <c>Ausflug Hamburg</c>).</summary>
    public static (int? Year, string Title) ParseYear(string name)
    {
        var match = YearRegex().Match(name.Trim());
        return match.Success ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value.Trim()) : (null, name.Trim());
    }

    /// <summary>
    /// The folder of an import that is an album a person made: the source folder without folders that are only a day (<c>2010 Urlaub Berlin/20100127</c> →
    /// <c>2010 Urlaub Berlin</c>); null when what is left names no album (no folder, <c>DCIM/100CANON</c>, <c>WhatsApp Images</c>, <c>Fotos/2010</c>).
    /// </summary>
    public static string? AlbumFolderOf(string? sourceFolder)
    {
        var parts = (sourceFolder ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        while (parts.Count > 0 && Parse(parts[^1]) is { Date: not null, Title.Length: 0 })
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count == 0 || GenericRegex().IsMatch(parts[^1]) ? null : string.Join('/', parts);
    }

    /// <summary>The last part of a folder path (<c>2023/2023.11.09 Hochzeit</c> → <c>2023.11.09 Hochzeit</c>; the top is empty).</summary>
    public static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The folder a file lies in (<c>a/b/c.jpg</c> → <c>a/b</c>).</summary>
    public static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}

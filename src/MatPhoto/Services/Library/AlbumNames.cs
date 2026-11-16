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

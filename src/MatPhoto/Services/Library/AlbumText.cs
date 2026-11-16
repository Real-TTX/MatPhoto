using System.Globalization;

namespace MatPhoto.Services.Library;

/// <summary>How the dates of an album read: "9 Nov 2023", "9 – 12 Nov 2023", "28 Dec 2023 – 2 Jan 2024".</summary>
public static class AlbumText
{
    public static string Dates(DateOnly? albumDate, DateTime? start, DateTime? end)
    {
        var culture = CultureInfo.CurrentUICulture;
        if (start is not { } s)
        {
            return albumDate?.ToString("d MMM yyyy", culture) ?? string.Empty;
        }

        var e = end ?? s;
        if (s.Date == e.Date)
        {
            return s.ToString("d MMM yyyy", culture);
        }

        if (s.Year == e.Year && s.Month == e.Month)
        {
            return $"{s.Day.ToString(culture)} – {e.ToString("d MMM yyyy", culture)}";
        }

        return s.Year == e.Year
            ? $"{s.ToString("d MMM", culture)} – {e.ToString("d MMM yyyy", culture)}"
            : $"{s.ToString("d MMM yyyy", culture)} – {e.ToString("d MMM yyyy", culture)}";
    }
}

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

    /// <summary>
    /// Where the photos of a group were taken, as information: the towns most photos have (at most <paramref name="max"/>, "+2" for the others) and the country
    /// when they all lie in one ("Berlin, Potsdam · Germany"); null when no photo knows its place.
    /// </summary>
    public static string? Places(IEnumerable<(string? City, string? Country)> photos, int max = 3)
    {
        var known = photos.Where(p => p.City is not null || p.Country is not null).ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var towns = known.Where(p => p.City is not null).GroupBy(p => p.City!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();
        var countries = known.Where(p => p.Country is not null).Select(p => p.Country!).Distinct().ToList();
        var text = string.Join(", ", towns.Take(max)) + (towns.Count > max ? $" +{towns.Count - max}" : string.Empty);
        if (countries.Count == 1)
        {
            return text.Length == 0 ? countries[0] : $"{text} · {countries[0]}";
        }

        return text.Length == 0 ? string.Join(", ", countries.Take(max)) : text;
    }
}

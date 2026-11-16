using System.Globalization;
using System.IO.Compression;

namespace MatPhoto.Services.Library;

/// <summary>A place of the world: a town and the region and country it lies in.</summary>
public sealed record Place(string City, string? Region, string CountryCode, string Country, double Latitude, double Longitude);

/// <summary>
/// Finds the town nearest to a position, offline: the towns of 15,000 people or more of GeoNames (<c>Resources/Geo</c>, CC BY 4.0), about 34,000, kept in memory in
/// cells of one degree. Nothing is asked from another server, so the places of the photos never leave the installation.
/// </summary>
public static class GeoPlaces
{
    /// <summary>Further than this from any town counts as "nowhere" (the sea, the mountains): no place.</summary>
    public const double MaxDistanceKm = 60;

    private static readonly Lazy<Index> Data = new(Load);

    /// <summary>The town nearest to the position, or null when there is none within <see cref="MaxDistanceKm"/> (or the position is not on the earth).</summary>
    public static Place? Nearest(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180 || (latitude == 0 && longitude == 0))
        {
            return null;
        }

        var index = Data.Value;
        Place? best = null;
        var bestKm = MaxDistanceKm;
        var row = (int)Math.Floor(latitude);
        var column = (int)Math.Floor(longitude);
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var cx = column + dx;
                cx = cx < -180 ? cx + 360 : cx >= 180 ? cx - 360 : cx;
                if (!index.Cells.TryGetValue(Key(row + dy, cx), out var places))
                {
                    continue;
                }

                foreach (var place in places)
                {
                    var km = DistanceKm(latitude, longitude, place.Latitude, place.Longitude);
                    if (km < bestKm)
                    {
                        bestKm = km;
                        best = place;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>The name of a country in the language of the request (the culture data of the system), else the English one of GeoNames.</summary>
    public static string CountryName(string? code, string? fallback = null)
    {
        if (string.IsNullOrEmpty(code))
        {
            return fallback ?? string.Empty;
        }

        try
        {
            var region = new RegionInfo(code);
            return region.DisplayName;
        }
        catch (ArgumentException)
        {
            return fallback ?? (Data.Value.Countries.TryGetValue(code, out var name) ? name : code);
        }
    }

    /// <summary>The great-circle distance in kilometres.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static int Key(int row, int column) => (row + 90) * 360 + column + 180;

    private sealed record Index(Dictionary<int, List<Place>> Cells, Dictionary<string, string> Countries);

    private static Index Load()
    {
        var assembly = typeof(GeoPlaces).Assembly;
        var countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var stream = assembly.GetManifestResourceStream("MatPhoto.Resources.Geo.countries.tsv"))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 2)
                    {
                        countries[parts[0]] = parts[1];
                    }
                }
            }
        }

        var cells = new Dictionary<int, List<Place>>();
        using (var stream = assembly.GetManifestResourceStream("MatPhoto.Resources.Geo.cities.tsv.gz"))
        {
            if (stream is not null)
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                while (reader.ReadLine() is { } line)
                {
                    var p = line.Split('\t');
                    if (p.Length < 5 || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                        || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    {
                        continue;
                    }

                    var place = new Place(p[0], p[4].Length == 0 ? null : p[4], p[3], countries.GetValueOrDefault(p[3], p[3]), lat, lon);
                    var key = Key((int)Math.Floor(lat), (int)Math.Floor(lon));
                    if (!cells.TryGetValue(key, out var list))
                    {
                        cells[key] = list = [];
                    }

                    list.Add(place);
                }
            }
        }

        return new Index(cells, countries);
    }
}

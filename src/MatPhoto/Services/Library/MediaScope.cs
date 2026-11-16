using System.Globalization;

namespace MatPhoto.Services.Library;

/// <summary>
/// Which photos a grid shows, as a short text the pages and the scripts pass on: <c>all</c>, <c>album:12</c>, <c>list:4</c>, <c>favorites</c>, <c>videos</c>,
/// <c>screenshots</c>, <c>selfies</c>, <c>panoramas</c>, <c>raw</c>, <c>animated</c>, <c>live</c>, <c>place:DE</c>, <c>place:DE/Berlin</c>, <c>camera:iPhone 15 Pro</c>,
/// <c>year:2023</c>, <c>search:hochzeit 2023</c>, <c>nodate</c>, <c>noplace</c>.
/// </summary>
public sealed record MediaScope(string Kind, long? Id = null, string? Value = null)
{
    public static readonly MediaScope All = new("all");

    /// <summary>The automatic albums by type (Explore), in the order shown.</summary>
    public static readonly string[] TypeKinds = ["videos", "screenshots", "selfies", "panoramas", "live", "animated", "raw", "nodate", "noplace"];

    public bool IsAlbum => Kind == "album";

    public bool IsList => Kind == "list";

    /// <summary>A list keeps its own order; an album goes from the first photo to the last; everything else is newest first.</summary>
    public bool Ascending => IsAlbum;

    public static MediaScope Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        var colon = value.IndexOf(':');
        var kind = (colon < 0 ? value : value[..colon]).ToLowerInvariant();
        var rest = colon < 0 ? null : value[(colon + 1)..].Trim();
        return kind switch
        {
            "album" or "list" when long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 => new MediaScope(kind, id),
            "place" when rest is { Length: >= 2 } && rest[..2].All(char.IsAsciiLetter) => new MediaScope(kind, Value: rest.Length > 202 ? rest[..202] : rest),
            "camera" when !string.IsNullOrWhiteSpace(rest) => new MediaScope(kind, Value: rest.Length > 100 ? rest[..100] : rest),
            "year" when int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1800 and <= 2200 => new MediaScope(kind, year),
            "search" when !string.IsNullOrWhiteSpace(rest) => new MediaScope(kind, Value: rest.Length > 200 ? rest[..200] : rest),
            "favorites" => new MediaScope(kind),
            _ when TypeKinds.Contains(kind) => new MediaScope(kind),
            _ => All,
        };
    }

    /// <summary>The country of a place scope (<c>DE</c>) and the town, if any.</summary>
    public (string Country, string? City) Place
    {
        get
        {
            var value = Value ?? string.Empty;
            var slash = value.IndexOf('/');
            return slash < 0 ? (value.ToUpperInvariant(), null) : (value[..slash].ToUpperInvariant(), value[(slash + 1)..]);
        }
    }

    public override string ToString() => Kind switch
    {
        "album" or "list" or "year" => $"{Kind}:{Id}",
        "place" or "camera" or "search" => $"{Kind}:{Value}",
        _ => Kind,
    };
}

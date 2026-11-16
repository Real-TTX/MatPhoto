namespace MatPhoto.Pages;

/// <summary>
/// The photo grid of a page (the partial <c>_PhotoGrid</c>, driven by <c>wwwroot/js/photos.js</c>): which photos (<see cref="Scope"/>, see <c>MediaScope</c>), whether
/// the selection may take photos off a list (<see cref="ListId"/>) or make one the cover of an album (<see cref="AlbumId"/>), and what an empty grid says.
/// </summary>
public sealed record GridModel(string Scope, string Label, string EmptyIcon, string EmptyTitle, string EmptyText, string? EmptyAction = null, string? EmptyActionLabel = null)
{
    public long? ListId { get; init; }

    public long? AlbumId { get; init; }

    /// <summary>Days get a head of their own (the timeline, an album); a list keeps its order without them.</summary>
    public bool Days { get; init; } = true;
}

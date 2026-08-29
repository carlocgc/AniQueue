namespace AniQueue.Core.Domain;

/// <summary>
/// Which size of a picture this row is. Separate from <see cref="ImageKind"/>,
/// which says what the picture shows.
///
/// Stored as an integer; values are a database contract. Append only — reordering
/// or removing one is a data break, not a rename.
/// </summary>
public enum ImageRendition
{
    /// <summary>AniList's <c>medium</c>, 100px wide and around 9.7 KB. For a list slot.</summary>
    Thumbnail = 0,

    /// <summary>AniList's <c>extraLarge</c>, 460px wide and around 83.3 KB. For the detail dialog.</summary>
    Full = 1
}

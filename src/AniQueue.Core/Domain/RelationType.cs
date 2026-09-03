namespace AniQueue.Core.Domain;

/// <summary>
/// How one title relates to another, as the source states it. A subset of
/// AniList's <c>MediaRelation</c>: relations pointing at manga and novels, ones
/// that only share a character, and ones AniList leaves undefined are not stored,
/// because every relative the user sees carries a name for why it is there.
///
/// A type unknown to this enum is dropped at parse time rather than stored as a
/// catch-all.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum RelationType
{
    /// <summary>Comes before this title in the same work.</summary>
    Prequel = 1,

    /// <summary>Continues this title.</summary>
    Sequel = 2,

    /// <summary>The main work this one branches from.</summary>
    Parent = 3,

    /// <summary>A branch off the main work — an OVA, a side series.</summary>
    SideStory = 4,

    /// <summary>Another telling of the same story: a remake, a second adaptation.</summary>
    Alternative = 5,

    /// <summary>A separate work set in the same world.</summary>
    SpinOff = 6,

    /// <summary>A recap. Watchable, rarely worth queueing.</summary>
    Summary = 7,

    /// <summary>Repackages other entries — a compilation film.</summary>
    Compilation = 8,

    /// <summary>The other end of <see cref="Compilation"/>: this title holds that one.</summary>
    Contains = 9
}

/// <summary>
/// Maps a <see cref="RelationType"/> to and from AniList's spelling, and decides
/// which of its values AniQueue keeps at all.
/// </summary>
public static class RelationTypes
{
    /// <summary>
    /// Every type this application stores. Anything absent — including AniList
    /// values deliberately declined — is dropped rather than mapped to a default.
    /// </summary>
    private static readonly Dictionary<string, RelationType> ByAniListName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PREQUEL"] = RelationType.Prequel,
        ["SEQUEL"] = RelationType.Sequel,
        ["PARENT"] = RelationType.Parent,
        ["SIDE_STORY"] = RelationType.SideStory,
        ["ALTERNATIVE"] = RelationType.Alternative,
        ["SPIN_OFF"] = RelationType.SpinOff,
        ["SUMMARY"] = RelationType.Summary,
        ["COMPILATION"] = RelationType.Compilation,
        ["CONTAINS"] = RelationType.Contains
    };

    /// <summary>The type AniList named, or null if AniQueue does not store it.</summary>
    public static RelationType? FromAniList(string? name) =>
        name is not null && ByAniListName.TryGetValue(name, out var type) ? type : null;

    /// <summary>
    /// The same relation seen from the other title. Edges are stored exactly as
    /// fetched and inverted on read, so a title whose own relations have never been
    /// fetched is still reachable through the far end of another title's edge.
    /// </summary>
    /// <remarks>
    /// Types AniList publishes no inverse for are returned unchanged, so the worst
    /// case is a label that reads oddly from one side rather than one that is wrong.
    /// </remarks>
    public static RelationType Invert(RelationType type) => type switch
    {
        RelationType.Prequel => RelationType.Sequel,
        RelationType.Sequel => RelationType.Prequel,
        RelationType.Parent => RelationType.SideStory,
        RelationType.SideStory => RelationType.Parent,
        RelationType.Compilation => RelationType.Contains,
        RelationType.Contains => RelationType.Compilation,
        _ => type
    };

    /// <summary>How the type is written for a person, in the label beside a title.</summary>
    public static string Describe(RelationType type) => type switch
    {
        RelationType.Prequel => "Prequel",
        RelationType.Sequel => "Sequel",
        RelationType.Parent => "Parent story",
        RelationType.SideStory => "Side story",
        RelationType.Alternative => "Alternative version",
        RelationType.SpinOff => "Spin-off",
        RelationType.Summary => "Recap",
        RelationType.Compilation => "Compilation",
        RelationType.Contains => "Includes",
        _ => "Related"
    };
}

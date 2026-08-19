namespace AniQueue.Core.Domain;

/// <summary>
/// How one title relates to another, as the source states it.
///
/// Stored as an integer; values are a database contract. Append only.
///
/// This is a subset of AniList's <c>MediaRelation</c>, and the omissions are
/// decisions rather than gaps (D24). <c>CHARACTER</c> links shows that share a
/// character and nothing else, which is noise wearing a relation's clothes.
/// <c>ADAPTATION</c> and <c>SOURCE</c> point at manga and novels, which this
/// application does not hold. <c>OTHER</c> is undefined by construction, and a
/// relation that cannot be labelled cannot be shown — every relative the user sees
/// carries a name for why it is there.
///
/// A type unknown to this enum is dropped at parse time rather than stored as a
/// catch-all, so a value AniList adds later cannot arrive as a row nothing can
/// render.
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
    /// The same relation seen from the other title.
    /// </summary>
    /// <remarks>
    /// AniList states an edge from the perspective of the media that was queried, so
    /// an edge fetched as "A has sequel B" is the same fact as "B has prequel A".
    /// Edges are stored exactly as fetched and inverted on read, because normalising
    /// at write time would lose which end the source spoke from — and a title whose
    /// relations have never been fetched is reachable only through the far end of
    /// somebody else's edge.
    ///
    /// Three types are their own inverse. <see cref="Alternative"/> is symmetric by
    /// meaning, and <see cref="SpinOff"/> and <see cref="SideStory"/> are not, but
    /// AniList publishes no inverse for either — <c>PARENT</c> is the counterpart it
    /// uses for both, and choosing one would state a relationship the source did not.
    /// Left as they are, so the worst case is a label that reads oddly from one side
    /// rather than one that is wrong.
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

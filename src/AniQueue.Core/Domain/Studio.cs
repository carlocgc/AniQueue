namespace AniQueue.Core.Domain;

/// <summary>
/// One company, once, however many titles it worked on. The name is stored as
/// AniList publishes it; there is one source, so there is no second spelling to
/// reconcile against.
/// </summary>
public class Studio
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Whether this company animates, as opposed to funding or licensing. AniList
    /// returns animation studios and producers in one undifferentiated edge list,
    /// so this is what a surface filters on when no edge is flagged as main.
    /// </summary>
    public bool IsAnimationStudio { get; set; }

    public ICollection<AnimeStudio> Anime { get; set; } = [];
}

/// <summary>
/// A company worked on a title, and whether it was <i>the</i> studio. A join
/// entity rather than a pure join, because <see cref="IsMain"/> is a fact about the
/// pairing: a studio that is main on one title is a producer on the next.
/// </summary>
public class AnimeStudio
{
    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public int StudioId { get; set; }

    public Studio? Studio { get; set; }

    /// <summary>
    /// True for the studio AniList marks as primary. Not guaranteed to be true of
    /// any edge: a title with none flagged renders no studio line rather than an
    /// arbitrary one.
    /// </summary>
    public bool IsMain { get; set; }
}

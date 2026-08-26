namespace AniQueue.Core.Domain;

/// <summary>
/// One company, once, however many titles it worked on (D49).
/// </summary>
/// <remarks>
/// A table for <see cref="Genre"/>'s reasons. The name is stored as AniList publishes
/// it, with no attempt to reconcile "Production I.G" against any other spelling of it
/// — there is one source, so there is nothing to reconcile against.
/// </remarks>
public class Studio
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Whether this company animates, as opposed to funding or licensing.
    /// </summary>
    /// <remarks>
    /// A property of the company rather than of any one title's involvement, which is
    /// why it sits here and <see cref="AnimeStudio.IsMain"/> sits on the join. Stored
    /// because AniList returns animation studios and producers in one undifferentiated
    /// edge list, and a surface that wants to say "Studio" rather than "Companies"
    /// needs something to filter on when no edge is flagged as main.
    /// </remarks>
    public bool IsAnimationStudio { get; set; }

    public ICollection<AnimeStudio> Anime { get; set; } = [];
}

/// <summary>
/// A company worked on a title, and whether it was <i>the</i> studio.
/// </summary>
/// <remarks>
/// <b>A join entity rather than a pure join</b>, because <see cref="IsMain"/> is a
/// fact about the pairing and not about either side: a studio that is main on one
/// title is a producer on the next. It is the only thing separating an animation
/// studio from the companies that funded it in the single edge list AniList returns.
///
/// All edges are stored rather than filtering to the main one in the query. It is the
/// same query shape and the same migration either way, the marginal cost is a boolean
/// and a few thousand rows, and §10's studio-affinity idea — "you rate Kyoto Animation
/// 8.4" — then has its data already present rather than needing a refetch.
/// </remarks>
public class AnimeStudio
{
    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public int StudioId { get; set; }

    public Studio? Studio { get; set; }

    /// <summary>
    /// True for the studio AniList marks as primary.
    /// </summary>
    /// <remarks>
    /// Not guaranteed to be true of any edge. A title with none flagged renders no
    /// studio line rather than an arbitrary one — D25's silent degradation, which the
    /// art path has been following since 9a, applied to text.
    /// </remarks>
    public bool IsMain { get; set; }
}

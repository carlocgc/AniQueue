using AniQueue.Core.Domain;
using AniQueue.Core.Queue;

namespace AniQueue.Core.Library;

/// <summary>
/// One title the user owns, listed as part of another title's set.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="LibraryListItem"/>. A set is context rather than a
/// result: nothing is sorted or filtered from it, and the AI columns have no meaning
/// beside a title you are being shown for orientation. Reusing the row type would
/// carry every one of those fields into a query that has no use for them, and would
/// invite the list to grow a copy of the backlog's toolbar.
/// </remarks>
public sealed record RelatedTitle
{
    public required int AnimeId { get; init; }

    public required string Title { get; init; }

    public MediaType MediaType { get; init; }

    public int? EpisodeCount { get; init; }

    public int? EpisodeDurationMinutes { get; init; }

    public int? ReleaseYear { get; init; }

    /// <summary>
    /// The air date the ordering is built on, where it is known.
    /// </summary>
    /// <remarks>
    /// Kept on the record rather than used and discarded because it is the one
    /// piece of evidence for the order the user is looking at: two halves of a
    /// split-cour series share a year, so a list ordered by something finer than
    /// the year column has to be able to say what.
    /// </remarks>
    public DateOnly? StartDate { get; init; }

    public required LibraryStatus Status { get; init; }

    public int EpisodesWatched { get; init; }

    /// <summary>Whether this relative already occupies a slot in Up Next.</summary>
    public bool IsQueued { get; init; }

    /// <summary>
    /// How the source says the two are related, where it says anything at all.
    /// </summary>
    /// <remarks>
    /// Null is a real answer rather than missing data. Either the edges disagree —
    /// see <see cref="Label"/> — or the title is further than one edge away, as
    /// season one is from season three, and naming the relation would state
    /// something the source did not.
    /// </remarks>
    public RelationType? Relation { get; init; }

    /// <summary>
    /// What the tag beside the title reads.
    /// </summary>
    /// <remarks>
    /// "Related" is the honest label when the two ends describe the connection
    /// differently, which AniList's own vocabulary makes routine: it publishes
    /// <c>PARENT</c> as the counterpart of both <c>SIDE_STORY</c> and
    /// <c>SPIN_OFF</c>, so a pair can arrive as a spin-off read one way and a side
    /// story read the other. Picking a winner would state a relationship the source
    /// did not, and picking the first one seen would make the label depend on row
    /// order.
    /// </remarks>
    public string Label => Relation is { } relation ? RelationTypes.Describe(relation) : "Related";

    /// <summary>Estimated minutes to watch, or null when it cannot be known.</summary>
    public int? EstimatedRuntimeMinutes => RuntimeCalculator.Estimate(EpisodeCount, EpisodeDurationMinutes);
}

/// <summary>
/// Answers what one title comes with: the set a complete box set would hold.
///
/// There are no groups here and there will not be. This is keyed by anime id and
/// answered per title, because a set is a property of the title you asked about
/// rather than a thing stored anywhere.
/// </summary>
/// <remarks>
/// The set is the same work, followed as far as it goes. Prequel and sequel give the
/// main run and side story hangs the specials off it, so the walk follows those
/// transitively and stops at nothing else. Spin-offs, alternative versions, recaps
/// and compilations are not in the box.
///
/// A parent edge is not followed, which is what stops a spin-off dragging in the
/// work it branches from: AniList spells both with <c>PARENT</c>, so the edge cannot
/// tell them apart. See the note on <c>SameWork</c> in the implementation.
/// </remarks>
public interface IRelationService
{
    /// <summary>
    /// The set one title belongs to, in release order, excluding the title itself.
    /// </summary>
    /// <remarks>
    /// <b>Owned titles only.</b> The walk reaches thousands of titles the user has
    /// never expressed an interest in, and the only action AniQueue could offer for
    /// one of those is "go and add this somewhere else yourself". It walks
    /// through them, though: an unowned middle season is a gap in the results rather
    /// than a wall, because the walk happens in external identifiers and resolves to
    /// library rows only at the end.
    ///
    /// <b>Every status.</b> A completed prequel is the most useful thing this can
    /// say — it is why the title in front of you makes sense.
    ///
    /// <b>Release order, and nothing else.</b> AniList publishes no viewing
    /// sequence, and a topological sort along prequel edges produces story order,
    /// which is frequently the wrong watch order. Release order is a fact the source
    /// supplies; story order would be an opinion. Unknown dates sort last.
    ///
    /// <see cref="RelatedTitle.Relation"/> is set only for titles one edge away and
    /// null for anything further — see the note on that property.
    /// </remarks>
    Task<IReadOnlyList<RelatedTitle>> GetRelatedAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many titles <see cref="QueueSetAsync"/> would put in the queue, so the
    /// action can name its own size before anybody presses it.
    /// </summary>
    /// <remarks>
    /// Counts what would actually be appended — owned, still Planning, not already
    /// queued — so "queue five titles" is a promise rather than an estimate. The
    /// title the dialog is open on counts too, because it is part of its own set.
    /// Zero means the action is not worth offering at all: everything in the set is
    /// already queued or already watched.
    /// </remarks>
    Task<int> CountToQueueAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a title and the whole set it belongs to, in release order.
    /// </summary>
    /// <remarks>
    /// <b>Both directions, unlike the sequel walk this replaces.</b> That one went
    /// forward only, on the grounds that prequels are seasons the user has already
    /// watched. They are not always, and an unwatched prequel is the single best
    /// reason not to start here — while status already excludes the watched ones,
    /// because a Completed season is refused by the queue whichever direction it was
    /// reached from. The direction rule was doing work status was doing anyway.
    ///
    /// <b>Recaps and compilations are skipped</b> even when the walk runs through
    /// them, because a recap film linked as the sequel of one season and the prequel
    /// of the next is exactly how AniList threads them — and nobody asking for the
    /// rest of a series means the summary of the part they just watched.
    ///
    /// <b>It writes nothing itself.</b> The ordered set goes to
    /// <see cref="IQueueService.AddAnimeAsync"/>, which is what keeps the contiguity
    /// invariant in one place, and which decides queue eligibility here exactly as it
    /// does for a single press — so the result accounts for a Completed season the
    /// walk passed through rather than silently dropping it.
    /// </remarks>
    Task<QueueAddResult> QueueSetAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default);
}

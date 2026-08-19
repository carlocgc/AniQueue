using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>
/// One title the user owns, shown against another it is related to (D24).
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="LibraryListItem"/>. An expansion is context
/// rather than a result: nothing is selected, queued, hidden or sorted from it,
/// and the AI columns have no meaning beside a relative. Reusing the row type
/// would carry every one of those fields into a query that has no use for them,
/// and would invite the expansion to grow a copy of the toolbar above it.
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
    /// How the source says the two are related, or null when the edges disagree.
    /// </summary>
    /// <remarks>
    /// Null is a real answer rather than missing data — see <see cref="Label"/>.
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
/// Answers what a title is related to, for the rows the backlog is showing (D24).
///
/// There are no groups here and there will not be. A title's relatives are a
/// property of that title, read one edge out, which is why this is a lookup keyed
/// by anime id rather than anything that returns a set of sets.
/// </summary>
/// <remarks>
/// Split into a count and a detail deliberately, and the split is what makes the
/// page affordable. Fifty rows need to know only whether they have anything to
/// show — a row with no relatives shows no chevron at all, because a control that
/// sometimes does nothing teaches people to stop pressing it — and that is one
/// grouped query. The rest is loaded when somebody actually expands a row.
/// </remarks>
public interface IRelationService
{
    /// <summary>
    /// How many displayable relatives each of the given titles has.
    /// </summary>
    /// <remarks>
    /// One query for the whole page, and it counts exactly what an expansion would
    /// list: owned, not hidden, one edge out, the title itself excluded. A badge
    /// that promised more than the panel below it opened would be worse than no
    /// badge, so the two share their definition rather than their nerve.
    ///
    /// Titles with nothing are absent from the result rather than present as zero.
    /// </remarks>
    Task<IReadOnlyDictionary<int, int>> GetRelatedCountsAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What one title is related to, tagged and in release order.
    /// </summary>
    /// <remarks>
    /// <b>One edge out, never transitive.</b> Season five is not the sequel of
    /// season one, and a walk that kept going would pull an entire franchise into a
    /// panel opened to answer a much smaller question.
    ///
    /// <b>Owned titles only.</b> Relations reach thousands of titles the user has
    /// never expressed an interest in, and the only action AniQueue could offer for
    /// one of those is "go and add this somewhere else yourself" (D11).
    ///
    /// <b>Every status except hidden.</b> A completed prequel is the most useful
    /// thing an expansion can say — it is why the title in front of you makes sense
    /// — so an expansion is not filtered the way results are. Hidden is the one
    /// exception, because hiding is the user saying they do not want to see it.
    ///
    /// <b>Release order, and nothing else.</b> AniList publishes no viewing
    /// sequence, and a topological sort along prequel edges produces *story* order,
    /// which is frequently the wrong watch order. Release order is a fact the source
    /// supplies; story order would be an opinion (D24). Unknown dates sort last.
    /// </remarks>
    Task<IReadOnlyList<RelatedTitle>> GetRelatedAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default);
}

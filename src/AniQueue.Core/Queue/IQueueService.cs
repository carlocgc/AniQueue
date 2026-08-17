using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Queue;

/// <summary>
/// What adding to the queue did, with a reason for everything it declined.
/// </summary>
/// <remarks>
/// The reasons are separated rather than summed into one "skipped" count because
/// they are the answer to a question the user will ask: selecting five titles and
/// getting three is confusing until something says which two did not go, and why.
/// </remarks>
public sealed record QueueAddResult
{
    public required int Added { get; init; }

    /// <summary>Already had a slot. Adding twice is a no-op, not an error.</summary>
    public int AlreadyQueued { get; init; }

    /// <summary>
    /// No longer waiting to be watched — started, finished, on hold or dropped.
    /// </summary>
    /// <remarks>
    /// The queue holds what the user intends to watch next, so a title that has
    /// left Planning does not belong in it. Declining up front is the same rule
    /// <see cref="IQueueService.AdvanceAsync"/> applies afterwards; without it, a
    /// watched title could be queued and would then be deleted by the next import,
    /// which is a slot with a hidden expiry.
    /// </remarks>
    public int NoLongerPlanned { get; init; }

    /// <summary>
    /// Not in this profile's library at all, so there is nothing to plan. A stale
    /// selection, or a title removed since the page was rendered.
    /// </summary>
    public int Unavailable { get; init; }

    public int Skipped => AlreadyQueued + NoLongerPlanned + Unavailable;
}

/// <summary>One slot as the Up Next page shows it: a single title, in a position.</summary>
public sealed record QueueListItem
{
    public required int QueueItemId { get; init; }

    /// <summary>Zero-based; the UI shows <c>Position + 1</c>.</summary>
    public required int Position { get; init; }

    public required int AnimeId { get; init; }

    public required string Title { get; init; }

    public MediaType MediaType { get; init; }

    public int? EpisodeCount { get; init; }

    public int? ReleaseYear { get; init; }

    /// <summary>Null when the title has no library entry, which should not happen.</summary>
    public LibraryStatus? Status { get; init; }

    public int EpisodesWatched { get; init; }

    public AnimeSource Source { get; init; }

    public string? SourceAnimeId { get; init; }

    /// <summary>
    /// The franchise this title belongs to, if any.
    /// </summary>
    /// <remarks>
    /// Carried so the queue can badge the seasons of one franchise as visibly
    /// related. That badge is the whole of a franchise's presence here now: the
    /// rows are grouped to the eye, and independent to the ordering (D15).
    /// </remarks>
    public string? FranchiseName { get; init; }

    /// <summary>Estimated minutes to watch, or null when it cannot be known.</summary>
    public int? EstimatedRuntimeMinutes { get; init; }

    /// <summary>Link out to the site this title came from, if there is one.</summary>
    public SourceLink? SourceLink => SourceLinkBuilder.ForAnime(Source, SourceAnimeId);
}

/// <summary>A franchise with titles that could be queued, for the add control.</summary>
/// <param name="QueueableCount">
/// How many titles queueing it would actually add — its members that are still
/// planned, not already queued, and not optional. Shown rather than the total
/// membership, because that is the number the click will produce.
/// </param>
public sealed record QueueableFranchise(int FranchiseId, string Name, int QueueableCount);

/// <summary>
/// The manually ordered Up Next queue: the list of what to watch next, in the
/// order the user put it in.
///
/// Ordering is the whole point of the application (D11), so this is the service
/// that has to be right. Positions are contiguous zero-based integers, and that is
/// an invariant of this type alone — the database deliberately does not defend it
/// (D2), because SQLite checks uniqueness per statement and any block shift would
/// collide against itself mid-transaction. Every mutation here therefore runs in
/// one transaction and rewrites positions from the resulting order, which also
/// repairs a queue that arrived non-contiguous for any other reason.
///
/// Every slot is a single title (D15). Franchises are queued by expansion, not by
/// occupying a slot of their own.
/// </summary>
public interface IQueueService
{
    /// <summary>The whole queue in order. Small by design; not paged.</summary>
    Task<IReadOnlyList<QueueListItem>> GetQueueAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends titles to the end of the queue.
    /// </summary>
    /// <remarks>
    /// One rule governs queue membership: <b>a slot holds a title the user still
    /// plans to watch.</b> This method applies it when a title goes in, and
    /// <see cref="AdvanceAsync"/> applies the same rule again as statuses change.
    /// Anything already queued, already started or finished, or absent from the
    /// library is declined and counted, never added.
    ///
    /// It does not prevent re-watching, and the way it doesn't is the point: set the
    /// title back to Planning at the source and it becomes queueable again. D12 has
    /// AniQueue observe watch status rather than author it, so a re-watch is
    /// expressed where every other status change is, instead of as an exception
    /// carved out here.
    ///
    /// Adding something twice remains a no-op rather than an error, because from the
    /// backlog the user cannot always see what is already queued.
    /// </remarks>
    Task<QueueAddResult> AddAnimeAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a franchise by appending its titles individually, in viewing order.
    /// </summary>
    /// <remarks>
    /// This is D15's mechanic, and the reason a franchise needs no slot type of its
    /// own. One click still expresses one decision — "I want to watch Slayers" —
    /// but what lands in the queue is a run of things the user can actually sit
    /// down to, each independently orderable. Putting a film between two seasons
    /// becomes an ordinary drag rather than something the model forbids.
    ///
    /// Three filters, in order: members still <see cref="LibraryStatus.Planning"/>,
    /// because there is no point queueing what has been watched; members not
    /// already queued, so re-adding after a new season syncs adds only the new one;
    /// and, unless <paramref name="includeOptional"/> is set, members not marked
    /// <see cref="Anime.OptionalWithinFranchise"/> — the specials and side films the
    /// user has said are skippable.
    ///
    /// Ordering is by <see cref="Anime.FranchiseOrder"/>, with unsequenced members
    /// last and a title tiebreak, so the run is watchable top to bottom.
    /// </remarks>
    Task<QueueAddResult> AddFranchiseAsync(
        int profileId,
        int franchiseId,
        bool includeOptional = false,
        CancellationToken cancellationToken = default);

    /// <summary>Which of the profile's titles already occupy a queue slot.</summary>
    Task<IReadOnlySet<int>> GetQueuedAnimeIdsAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>Franchises that would actually add something, and how much.</summary>
    Task<IReadOnlyList<QueueableFranchise>> GetQueueableFranchisesAsync(
        int profileId,
        bool includeOptional = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a slot out of the queue and closes the gap it leaves.
    /// </summary>
    /// <returns>False when the slot does not exist, or belongs to another profile.</returns>
    Task<bool> RemoveAsync(
        int profileId,
        int queueItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the slots of anything that is no longer waiting to be watched, and
    /// closes the gaps, so that whatever is next really is next.
    /// </summary>
    /// <remarks>
    /// This is D12 made mechanical. AniQueue observes watched status rather than
    /// authoring it, so there is no "start watching" button to dequeue anything —
    /// starting a show is instead observed, as the entry ceasing to be Planning at
    /// the source. Advancement is what turns that observation into a queue that
    /// stays true without anyone maintaining it.
    ///
    /// It lives here rather than in the importer because it is a property of the
    /// queue, not of any one way of learning about a status change. Import calls it
    /// today; the Phase 5 sync will call the same method, and changes only how often
    /// it runs.
    ///
    /// Since D15 every slot is one title, so the rule is simply per title — watch
    /// the second season of something and only that row leaves, with the third
    /// rising to meet you. The bespoke "release a franchise once nothing in it is
    /// still planned" rule this needed under the old model is gone.
    ///
    /// Idempotent, and safe to call when nothing has changed.
    /// </remarks>
    /// <returns>How many slots were released.</returns>
    Task<int> AdvanceAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a slot one step, or to either end.
    /// </summary>
    /// <returns>
    /// False when the slot is unknown or the move changes nothing — already top and
    /// asked to go up, and so on. Callers use it to decide whether to re-read.
    /// </returns>
    Task<bool> MoveAsync(
        int profileId,
        int queueItemId,
        QueueMove move,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a slot to an explicit zero-based position — what a drag produces.
    /// </summary>
    /// <remarks>
    /// A destination past either end is clamped, not rejected: the browser's idea of
    /// the queue length can legitimately lag the server's, and a drop past the last
    /// row unambiguously means "put it last".
    /// </remarks>
    Task<bool> ReorderAsync(
        int profileId,
        int queueItemId,
        int targetPosition,
        CancellationToken cancellationToken = default);
}

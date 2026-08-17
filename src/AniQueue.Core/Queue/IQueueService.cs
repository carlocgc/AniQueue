using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Queue;

/// <summary>What adding to the queue did.</summary>
/// <param name="Added">Slots created.</param>
/// <param name="AlreadyQueued">Entries skipped because they already had a slot.</param>
public sealed record QueueAddResult(int Added, int AlreadyQueued);

/// <summary>
/// One slot as the Up Next page shows it.
///
/// A slot holds either a title or a whole franchise (D1), so roughly half these
/// properties apply to any given row. That is preferred here over two record types
/// and a discriminated read at every call site: the page renders one ordered list
/// in one loop, and splitting the type would split the loop for no gain.
/// </summary>
public sealed record QueueListItem
{
    public required int QueueItemId { get; init; }

    /// <summary>Zero-based; the UI shows <c>Position + 1</c>.</summary>
    public required int Position { get; init; }

    /// <summary>The anime's title, or the franchise's name.</summary>
    public required string Title { get; init; }

    public int? AnimeId { get; init; }

    public int? FranchiseId { get; init; }

    public bool IsFranchise => FranchiseId is not null;

    /// <summary>Total minutes to watch, or null when it cannot be known.</summary>
    /// <remarks>
    /// Supplied by the service rather than derived here: a franchise's runtime is a
    /// sum over its members, which is not reconstructable from one row.
    /// </remarks>
    public int? EstimatedRuntimeMinutes { get; init; }

    /// <summary>
    /// True when the runtime above omits entries whose length is unknown. A total
    /// built from half a franchise is misleading unless the UI can say so.
    /// </summary>
    public bool IsRuntimePartial { get; init; }

    // --- Title slots -----------------------------------------------------

    public MediaType MediaType { get; init; }

    public int? EpisodeCount { get; init; }

    public int? ReleaseYear { get; init; }

    /// <summary>Null for a franchise slot, which has no single status.</summary>
    public LibraryStatus? Status { get; init; }

    public int EpisodesWatched { get; init; }

    public AnimeSource Source { get; init; }

    public string? SourceAnimeId { get; init; }

    /// <summary>Link out to the site this title came from, if there is one.</summary>
    public SourceLink? SourceLink =>
        IsFranchise ? null : SourceLinkBuilder.ForAnime(Source, SourceAnimeId);

    // --- Franchise slots -------------------------------------------------

    /// <summary>Titles in the franchise.</summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// How many of them are finished. Optional entries (<see
    /// cref="Anime.OptionalWithinFranchise"/>) are counted like any other here;
    /// treating them as skippable is part of Phase 5's completion maths, and
    /// anticipating it would mean two definitions of "done" in the codebase at
    /// once.
    /// </summary>
    public int CompletedEntryCount { get; init; }
}

/// <summary>A franchise that could be queued, for the add control.</summary>
public sealed record QueueableFranchise(int FranchiseId, string Name, int EntryCount);

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
/// </summary>
public interface IQueueService
{
    /// <summary>The whole queue in order. Small by design; not paged.</summary>
    Task<IReadOnlyList<QueueListItem>> GetQueueAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends titles to the end of the queue, skipping any already present.
    ///
    /// Queue membership is deliberately idempotent: adding something twice is a
    /// no-op rather than an error, because from the backlog the user cannot always
    /// see what is already queued.
    /// </summary>
    Task<QueueAddResult> AddAnimeAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends whole franchises to the end of the queue, on the same terms.
    /// </summary>
    Task<QueueAddResult> AddFranchisesAsync(
        int profileId,
        IReadOnlyCollection<int> franchiseIds,
        CancellationToken cancellationToken = default);

    /// <summary>Which of the profile's titles already occupy a queue slot.</summary>
    Task<IReadOnlySet<int>> GetQueuedAnimeIdsAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>Franchises with at least one title, excluding those already queued.</summary>
    Task<IReadOnlyList<QueueableFranchise>> GetQueueableFranchisesAsync(
        int profileId,
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

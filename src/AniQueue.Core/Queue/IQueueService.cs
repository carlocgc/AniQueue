using AniQueue.Core.Progress;

namespace AniQueue.Core.Queue;

/// <summary>What adding to the queue did.</summary>
/// <param name="Added">Slots created.</param>
/// <param name="AlreadyQueued">Entries skipped because they already had a slot.</param>
public sealed record QueueAddResult(int Added, int AlreadyQueued);

/// <summary>
/// The manually ordered Up Next queue.
///
/// Phase 3 needs only enough of this to add from the backlog; reordering, the
/// move buttons and drag-and-drop arrive with Phase 4. Additions append to the
/// end, which is the one position that cannot be mistaken for an opinion about
/// priority — the user decides the order afterwards.
/// </summary>
public interface IQueueService
{
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

    /// <summary>Which of the given titles already occupy a queue slot.</summary>
    Task<IReadOnlySet<int>> GetQueuedAnimeIdsAsync(
        int profileId,
        CancellationToken cancellationToken = default);
}

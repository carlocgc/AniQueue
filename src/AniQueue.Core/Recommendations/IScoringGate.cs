namespace AniQueue.Core.Recommendations;

/// <summary>
/// Keeps two scoring runs off one model at the same time, and decides which yields.
/// </summary>
/// <remarks>
/// A self-hosted model answers one request at a time. Without this, a sweep at three
/// in the morning and a <i>Rank now</i> pressed at ten past would queue behind each
/// other: the person waiting would watch a dialog tick for minutes with nothing to
/// explain it, and the sweep would carry on feeding batches in front of them for the
/// rest of the hour.
///
/// <b>The person wins, and the sweep loses nothing.</b> A sweep is resumable by
/// construction — it picks the least recently scored titles each time, so abandoning
/// it between batches costs one tick rather than the run. Interactive work is not
/// resumable: somebody is waiting on it.
///
/// So the sweep asks between batches whether anybody is waiting, and stops if they
/// are. What the person waits for is the batch already in flight, which is one request
/// rather than an hour of them.
/// </remarks>
public interface IScoringGate
{
    /// <summary>Whether a person is waiting for the model right now.</summary>
    /// <remarks>
    /// Read by the sweep between batches. It is deliberately a question about intent
    /// rather than about the lock: somebody blocked on <see cref="EnterInteractiveAsync"/>
    /// has not got the model yet, and waiting for the sweep to notice is the whole
    /// point of asking.
    /// </remarks>
    bool IsInteractiveWaiting { get; }

    /// <summary>Takes the model for a run somebody is watching.</summary>
    Task<IDisposable> EnterInteractiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes the model for one batch of an unattended sweep.</summary>
    /// <remarks>
    /// Held for a batch rather than for a sweep, which is what makes standing down
    /// possible at all: a lock held for the whole hour would make "the sweep yields"
    /// mean "the sweep yields in an hour".
    /// </remarks>
    Task<IDisposable> EnterSweepAsync(CancellationToken cancellationToken = default);
}

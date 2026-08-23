using AniQueue.Core.Domain;

namespace AniQueue.Core.Jobs;

/// <summary>
/// Keeps what background tasks have done, and answers when each last ran (D40).
/// </summary>
/// <remarks>
/// <b>The cadence is measured from here rather than from a job's own table.</b> Sync
/// used to read its due-ness from <c>SyncRun</c>, which only records runs that
/// reached a terminal state — so a run that was cancelled, or that threw before it
/// could write anything, left no trace the due check could see and the next tick
/// started it again. Reading the clock from the record every run writes is what makes
/// "cancel skips this cycle" true rather than aspirational, and it is the same answer
/// for every task instead of one per job.
/// </remarks>
public interface IJobRunStore
{
    /// <summary>
    /// Writes a run and prunes what has fallen off the end.
    /// </summary>
    /// <remarks>
    /// Only ever called with a run that happened. A tick that found the task not due
    /// is not a run, and recording those would bury the real ones: at a five-minute
    /// polling resolution against a daily cadence they are almost every tick.
    /// </remarks>
    Task RecordAsync(JobRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// When this unit last ran, whatever the run did, or null if it never has.
    /// </summary>
    /// <remarks>
    /// <b>Whatever it did</b> is the point. A failed run counts, because nothing
    /// reschedules itself any more and a task that failed should try again on its
    /// ordinary cadence rather than immediately (D40). A cancelled run counts, because
    /// cancelling means "not this cycle" rather than "never mind". A run that found
    /// nothing counts, because it happened.
    /// </remarks>
    Task<DateTimeOffset?> LastRunAtAsync(
        string taskKey,
        string? unitKey,
        CancellationToken cancellationToken = default);
}

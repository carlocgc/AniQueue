using AniQueue.Core.Domain;

namespace AniQueue.Core.Jobs;

/// <summary>
/// Keeps what background tasks have done, and answers when each last ran.
/// </summary>
/// <remarks>
/// The cadence is measured from here rather than from a job's own table. A run is
/// recorded here whether or not it reached a terminal state, so a cancelled or failed
/// run still skips its cycle instead of being restarted on the next tick, and the
/// answer is the same for every task.
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
    /// ordinary cadence rather than immediately. A cancelled run counts, because
    /// cancelling means "not this cycle" rather than "never mind". A run that found
    /// nothing counts, because it happened.
    /// </remarks>
    Task<DateTimeOffset?> LastRunAtAsync(
        string taskKey,
        string? unitKey,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent run of each unit, keyed by task and unit.</summary>
    /// <remarks>
    /// One query for the whole page rather than one per row. There are a handful of
    /// units and the index covers it either way, but a page that issues a query per
    /// row is a page that gets slower every time a job is added.
    /// </remarks>
    Task<IReadOnlyDictionary<(string TaskKey, string UnitKey), JobRun>> LatestAsync(
        CancellationToken cancellationToken = default);

    /// <summary>The newest runs across every task, for the history card.</summary>
    Task<IReadOnlyList<JobRun>> RecentAsync(int limit, CancellationToken cancellationToken = default);
}

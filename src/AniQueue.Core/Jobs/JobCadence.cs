using AniQueue.Core.Domain;

namespace AniQueue.Core.Jobs;

/// <summary>
/// Whether a task's cadence has come round, in one place.
/// </summary>
/// <remarks>
/// Every task answers this the same way and each one used to answer it for itself,
/// which is three copies of one comparison and three chances for them to drift. What
/// stays with the task is what it does about the answer and what it says in the log.
///
/// The clock is read from <c>JobRun</c> rather than from whatever record the task
/// also keeps, because a run that failed, was cancelled or found nothing still
/// happened — and a task measured from its last <i>successful</i> run would be asked
/// again on the very next tick for as long as it kept not succeeding.
/// </remarks>
public static class JobCadence
{
    /// <summary>
    /// Whether a timed run should happen now.
    /// </summary>
    /// <param name="schedule">The one cadence every task shares.</param>
    /// <param name="lastRun">When this unit last ran, or null if it never has.</param>
    /// <param name="now">The current time.</param>
    /// <remarks>
    /// <see cref="SyncSchedule.Off"/> is never due. It is the absence of a schedule
    /// rather than a very long one, and a task with no schedule still runs when the
    /// library changes and whenever somebody asks — which is what the settings page
    /// means by stopping the clock and nothing else.
    /// </remarks>
    public static bool IsDue(SyncSchedule schedule, DateTimeOffset? lastRun, DateTimeOffset now) =>
        schedule.ToInterval() is { } interval
        && (lastRun is not { } previous || now - previous >= interval);
}

namespace AniQueue.Core.Jobs;

/// <summary>What woke a job, and which of its units is being asked.</summary>
/// <param name="Trigger">Why this run is happening.</param>
/// <param name="Unit">
/// Which schedulable unit to run, or null for a job that owns only one.
/// </param>
public readonly record struct JobRunContext(JobTrigger Trigger, string? Unit = null)
{
    /// <summary>
    /// Whether the job should ignore whether it is due.
    /// </summary>
    /// <remarks>
    /// A manual run is a cadence check brought forward, not a bigger run: it selects
    /// the same work a scheduled one would. A library change skips the check because
    /// it is not a scheduled run at all, and asking whether the cadence has elapsed
    /// would answer no and do nothing.
    ///
    /// This applies to a job that consumes library changes, not to one that produces
    /// them. Sync publishes when it commits and hears its own signal, so it reads
    /// <see cref="JobTrigger"/> directly rather than asking this.
    /// </remarks>
    public bool IgnoresSchedule => Trigger is not JobTrigger.Timer;

    /// <summary>
    /// Whether this run should take only work that has never been done, leaving what
    /// has merely gone stale.
    /// </summary>
    /// <remarks>
    /// True only for a library change, which is what stops a ten-second import
    /// turning into hours of somebody's GPU: an import lands hundreds of ratings at
    /// one timestamp, so every earlier score goes stale at once. Newly added titles
    /// are scored now; the re-score waits for the cadence.
    /// </remarks>
    public bool NewWorkOnly => Trigger is JobTrigger.LibraryChange;
}

/// <summary>Why a job is running.</summary>
/// <remarks>Stored on a run record; values are a database contract. Append only.</remarks>
public enum JobTrigger
{
    /// <summary>The cadence came round. The job still decides whether it is due.</summary>
    Timer = 0,

    /// <summary>Something changed the library, so a job with new work should not wait.</summary>
    LibraryChange = 1,

    /// <summary>Somebody pressed the button.</summary>
    Manual = 2
}

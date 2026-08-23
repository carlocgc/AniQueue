namespace AniQueue.Core.Jobs;

/// <summary>What woke a job, and which of its units is being asked (D40, D41).</summary>
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
    /// A manual run is a cadence check brought forward — the user is the timer
    /// (D41). It is deliberately not a <i>bigger</i> run: it selects the same work a
    /// scheduled one would, which is what makes pressing the button safe.
    ///
    /// A library change also skips the check, for the opposite reason: it is not a
    /// scheduled run at all, and asking "has the cadence elapsed" of a wake-up caused
    /// by new data would answer no and do nothing, which is the latency D28 exists to
    /// remove.
    ///
    /// <b>This applies to a job that consumes library changes, and not to one that
    /// produces them.</b> Sync is the producer: it publishes when it commits, every
    /// runner including its own hears that, and a sync that treated the signal as a
    /// reason to fetch would fetch in response to its own last fetch. It honours the
    /// cadence for a library change and bypasses it only for
    /// <see cref="JobTrigger.Manual"/>, which is why it reads
    /// <see cref="JobTrigger"/> directly rather than asking this.
    /// </remarks>
    public bool IgnoresSchedule => Trigger is not JobTrigger.Timer;

    /// <summary>
    /// Whether this run should take only work that has never been done, leaving what
    /// has merely gone stale.
    /// </summary>
    /// <remarks>
    /// True only for a library change, and this is the rule that stops a ten-second
    /// import turning into hours of somebody's GPU (D41). D39 records the case: a
    /// MyAnimeList import lands hundreds of ratings at one timestamp, so every score
    /// taken before it goes stale at once. Newly added titles are scored now; the
    /// re-score waits for the cadence, when nobody is standing over it.
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

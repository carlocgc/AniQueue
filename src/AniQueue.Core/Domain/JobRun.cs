using AniQueue.Core.Jobs;

namespace AniQueue.Core.Domain;

/// <summary>
/// One run of one background task, in the terms every task has in common: the
/// audit trail for work nobody watched, the source of the "last run" a task row
/// shows, and the clock the cadence is measured from.
/// </summary>
/// <remarks>
/// This does not replace a job's own typed record. <see cref="SyncRun"/> still
/// carries what only a sync can reason about — held conflicts, absent titles, the
/// counts the Sources page badges. What is here is only what a page listing every
/// task can render.
///
/// One table rather than a union over the typed ones, because their timestamps are
/// <c>DateTimeOffset</c>, which SQLite can neither order nor compare; this table
/// orders by <see cref="Id"/>, which for an insert-only table is the same order.
/// </remarks>
public class JobRun
{
    public int Id { get; set; }

    /// <summary>
    /// Which task this was, stably. Not the display name, which is allowed to
    /// change without orphaning the history it names.
    /// </summary>
    public required string TaskKey { get; set; }

    /// <summary>
    /// Which schedulable unit within the task, or empty for a task that owns one.
    /// </summary>
    /// <remarks>
    /// Empty rather than null: every lookup here is by unit, and EF translates a
    /// nullable comparison to <c>= @p</c>, which is never true of NULL. The
    /// contract keeps null for "the only unit"; the store normalises at the
    /// boundary.
    /// </remarks>
    public required string UnitKey { get; set; }

    /// <summary>What woke it.</summary>
    public JobTrigger Trigger { get; set; }

    /// <summary>
    /// When the run began, which is what the cadence is measured from — from the
    /// start rather than the finish, so a slow run does not push the next one out
    /// by its own duration.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public JobOutcome Outcome { get; set; }

    /// <summary>How many things the run considered.</summary>
    public int ItemsProcessed { get; set; }

    /// <summary>How many it actually changed.</summary>
    public int ItemsChanged { get; set; }

    /// <summary>
    /// Why a failed run failed, in plain words. Never a stack trace: this is
    /// rendered to whoever opens the page.
    /// </summary>
    public string? FailureReason { get; set; }
}

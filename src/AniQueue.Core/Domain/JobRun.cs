using AniQueue.Core.Jobs;

namespace AniQueue.Core.Domain;

/// <summary>
/// One run of one background task, in the terms every task has in common (D40).
///
/// The audit trail for work nobody watched, the source of the "last run" a task row
/// shows, and — since it is written for every run that happened — the clock the
/// cadence is measured from.
/// </summary>
/// <remarks>
/// <b>This does not replace a job's own typed record.</b> <see cref="SyncRun"/> still
/// carries what a sync needs to reason about — held conflicts, absent titles, the
/// counts the Sources page badges — because those mean nothing to an artwork fetch
/// and folding them in would rebuild the wide row of nullable columns D7 rejected.
/// What is here is only what a page listing every task can render.
///
/// <b>One table rather than a union over the typed ones</b>, and SQLite decides that
/// rather than tidiness: <c>SyncRun.StartedAt</c> and <c>RecommendationRun.CreatedAt</c>
/// are both <c>DateTimeOffset</c>, which SQLite can neither order nor compare, so a
/// merged and paged history would have to be sorted in memory over an unbounded set.
/// One table orders by <see cref="Id"/>, which for an insert-only table is the same
/// order.
///
/// <b>No <c>ProfileId</c>.</b> A background task belongs to the deployment rather
/// than to a profile — the runner is a hosted service, not something a user owns —
/// and the one place a profile appears is inside what the job goes on to do.
/// </remarks>
public class JobRun
{
    public int Id { get; set; }

    /// <summary>
    /// Which task this was, stably.
    /// </summary>
    /// <remarks>
    /// Deliberately not the job's display name. What a task is called is allowed to
    /// change — it is a label on a row somebody reads — and a rename must not orphan
    /// the history it names.
    /// </remarks>
    public required string TaskKey { get; set; }

    /// <summary>
    /// Which schedulable unit within the task, or empty for a task that owns one.
    /// </summary>
    /// <remarks>
    /// Empty rather than null, and that is a database concern rather than a modelling
    /// one: a nullable column would be compared against a nullable parameter, which
    /// EF translates to <c>= @p</c> and which is never true of NULL. Every lookup here
    /// is by unit, so the trap would be permanent and silent. The contract keeps null
    /// for "the only unit" because it reads better; the store normalises at the
    /// boundary.
    /// </remarks>
    public required string UnitKey { get; set; }

    /// <summary>What woke it (D41).</summary>
    public JobTrigger Trigger { get; set; }

    /// <summary>
    /// When the run began, which is what the cadence is measured from.
    /// </summary>
    /// <remarks>
    /// From the start rather than the finish, so a slow run does not push the next one
    /// out by its own duration — the rule <c>SyncRun</c> already followed and the
    /// reason an hourly schedule lands at 60–65 minutes rather than drifting.
    /// </remarks>
    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public JobOutcome Outcome { get; set; }

    /// <summary>How many things the run considered.</summary>
    public int ItemsProcessed { get; set; }

    /// <summary>How many it actually changed.</summary>
    public int ItemsChanged { get; set; }

    /// <summary>
    /// Why a failed run failed, in plain words. Never a stack trace: this is rendered
    /// to whoever opens the page (§6).
    /// </summary>
    public string? FailureReason { get; set; }
}

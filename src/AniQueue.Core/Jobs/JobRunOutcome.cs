namespace AniQueue.Core.Jobs;

/// <summary>
/// What one run of one unit did, in the terms every task has in common.
/// </summary>
/// <remarks>
/// Returned by the job rather than computed by the runner, so the run table and
/// whatever typed record the job also keeps cannot disagree about one event.
///
/// It holds only what every task shares: how many did you look at, how many did you
/// change, and did it work.
/// </remarks>
/// <param name="Outcome">How the run ended.</param>
/// <param name="ItemsProcessed">How many things the run considered.</param>
/// <param name="ItemsChanged">
/// How many it actually changed, which is <b>not necessarily a subset of what it
/// considered, nor even the same kind of thing</b>. A relation pass considers titles
/// and changes edges, so 540 considered and 826 changed is a correct pair. Anything
/// rendering both must not join them with a word like "of".
/// </param>
/// <param name="FailureReason">
/// Why a failed run failed, in plain words. Never a stack trace — this reaches a
/// page.
/// </param>
public sealed record JobRunOutcome(
    JobOutcome Outcome,
    int ItemsProcessed = 0,
    int ItemsChanged = 0,
    string? FailureReason = null)
{
    /// <summary>
    /// The run happened and there was nothing to do.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from a run that was not due, which is not a run at all
    /// and is never recorded. A converged task and a broken one are indistinguishable
    /// if the only thing a page can report is the last run that changed something —
    /// relations in its steady state legitimately does nothing for weeks.
    /// </remarks>
    public static JobRunOutcome NothingToDo { get; } = new(JobOutcome.NothingToDo);

    /// <summary>The tick came round and this unit was not due. Never recorded.</summary>
    public static JobRunOutcome NotDue { get; } = new(JobOutcome.NotDue);

    public static JobRunOutcome Succeeded(int processed, int changed) =>
        new(JobOutcome.Succeeded, processed, changed);

    public static JobRunOutcome Failed(string reason, int processed = 0, int changed = 0) =>
        new(JobOutcome.Failed, processed, changed, reason);

    /// <summary>Whether this run is worth a row.</summary>
    public bool IsRecordable => Outcome is not JobOutcome.NotDue;
}

/// <summary>How a run ended.</summary>
/// <remarks>Stored as an integer; values are a database contract. Append only.</remarks>
public enum JobOutcome
{
    /// <summary>Work was found and done.</summary>
    Succeeded = 0,

    /// <summary>The run happened and found nothing outstanding.</summary>
    NothingToDo = 1,

    /// <summary>It went wrong. <c>FailureReason</c> says how.</summary>
    Failed = 2,

    /// <summary>
    /// Somebody stopped it. Not a failure, and this distinction is load-bearing:
    /// counting a cancel as a failure would raise a stalled banner over a button the
    /// user pressed on purpose.
    /// </summary>
    Cancelled = 3,

    /// <summary>
    /// The tick came round and this unit was not due, so nothing ran.
    /// </summary>
    /// <remarks>
    /// Has a value because the runner has to distinguish it, and is never written to
    /// a run table: at a five-minute polling resolution against a daily cadence this
    /// is almost every tick, and recording them would bury the runs that happened.
    /// </remarks>
    NotDue = 4
}

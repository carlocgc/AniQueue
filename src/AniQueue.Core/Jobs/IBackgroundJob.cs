namespace AniQueue.Core.Jobs;

/// <summary>One schedulable unit of a job — what a row on the tasks page is (D40).</summary>
/// <param name="Key">
/// Identifies the unit to its own job, and nothing else. Null for a job that owns
/// only one.
/// </param>
/// <param name="Name">What to call it where somebody can see it.</param>
public sealed record JobUnit(string? Key, string Name);

/// <summary>
/// Work that happens on a timer with nobody watching.
///
/// The interface exists rather than a hand-rolled loop inside the sync runner
/// because AniQueue ends up with several of these — metadata and artwork
/// enrichment, and scheduled re-ranking — and the loop each of them needs is
/// identical: tick, open a scope, refuse to overlap, catch, log. Expressing that
/// once costs about the same as writing it for sync alone and makes the second job
/// additive rather than a refactor.
/// </summary>
/// <remarks>
/// <b>What a job records is still its own business.</b> A <c>SyncRun</c>'s columns
/// mean something for a sync and nothing for an artwork fetch, and folding every job
/// into one typed table would force a JSON blob or a wide row of nullable columns —
/// the stringly-typed bag D7 rejected. That rule stands; D40 narrows it. The typed
/// table stays where a job needs to <i>reason</i> about its own history, and
/// <see cref="JobRunOutcome"/> carries only what every task has in common, because
/// that is what a page listing every task can render.
/// </remarks>
public interface IBackgroundJob
{
    /// <summary>
    /// Identifies the job in the database, stably and forever.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Name"/>. What a task is called is a
    /// label somebody reads and is allowed to change; this is what its history is
    /// filed under, and a rename must not orphan it. Not the type name either, for
    /// the same reason a class rename should not be a data migration.
    /// </remarks>
    string Key { get; }

    /// <summary>Names the job in the log, and on its row.</summary>
    string Name { get; }

    /// <summary>
    /// How often the runner asks this job whether there is anything to do.
    /// </summary>
    /// <remarks>
    /// The <i>polling resolution</i>, not the schedule. A job decides for itself
    /// whether it is due, from a cadence that can change while the application is
    /// running — one that lived in the runner's timer could only take effect on
    /// restart. So this is short and cheap, and <see cref="RunAsync"/> returns
    /// <see cref="JobRunOutcome.NotDue"/> most of the time.
    ///
    /// It is <b>only</b> polling resolution now. Until D40 it was also the base unit
    /// of a failure backoff, which is why a long one was expensive; nothing
    /// reschedules itself any more.
    /// </remarks>
    TimeSpan TickPeriod { get; }

    /// <summary>
    /// The units this job runs, each of which is a row, a button and a history of its
    /// own. Never empty.
    /// </summary>
    /// <remarks>
    /// Sync has one per fetchable source, because each carries its own enabled state
    /// and its own failure history — one row covering both would have to aggregate
    /// two of everything, and <i>Run now</i> would mean "whichever of these are due"
    /// (D40). Everything else owns exactly one unit and returns a single entry whose
    /// key is null.
    ///
    /// Read once per tick from a job resolved in that tick's scope, so a unit list
    /// that depends on configuration follows the configuration without a restart.
    /// </remarks>
    IReadOnlyList<JobUnit> Units { get; }

    /// <summary>
    /// Does whatever is due for one unit, or nothing.
    /// </summary>
    /// <remarks>
    /// Called on a fresh scope each tick and never re-entered: the runner's loop is
    /// sequential, so a call that outlasts its tick period delays the next one rather
    /// than running beside it.
    ///
    /// <b>It returns what it did.</b> The runner records that rather than counting
    /// again, so the shared record and the job's own typed one cannot disagree (D40).
    /// A job that throws instead is still recorded, as a failure, because a run that
    /// left no trace would leave the cadence clock unmoved and throw again on the
    /// next tick forever.
    /// </remarks>
    Task<JobRunOutcome> RunAsync(JobRunContext context, CancellationToken cancellationToken);
}

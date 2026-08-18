namespace AniQueue.Core.Jobs;

/// <summary>
/// Work that happens on a timer with nobody watching.
///
/// The interface exists rather than a hand-rolled loop inside the sync runner
/// because AniQueue ends up with several of these — metadata and artwork
/// enrichment, and eventually scheduled re-ranking — and the loop each of them
/// needs is identical: tick, open a scope, refuse to overlap, catch, log, back
/// off. Expressing that once costs about the same as writing it for sync alone
/// and makes the second job additive rather than a refactor.
///
/// <b>Only the loop is generalised.</b> What a job records is its own business: a
/// <c>SyncRun</c>'s columns mean something for a sync and nothing for an artwork
/// fetch, and folding future jobs into one run table would force either a JSON
/// blob or a wide row of nullable columns each belonging to one job type — the
/// stringly-typed bag D7 rejected. A second job gets a second typed table.
/// </summary>
public interface IBackgroundJob
{
    /// <summary>Names the job in the log. Not shown to users.</summary>
    string Name { get; }

    /// <summary>
    /// How often the runner asks this job whether there is anything to do.
    /// </summary>
    /// <remarks>
    /// The <i>polling resolution</i>, not the schedule. A job decides for itself
    /// whether it is due — sync's schedule is a per-source user setting that can
    /// change while the application is running, and one that lived in the runner's
    /// timer could only take effect on restart. So this is short and cheap, and
    /// <see cref="RunAsync"/> returns immediately most of the time.
    /// </remarks>
    TimeSpan TickPeriod { get; }

    /// <summary>
    /// Does whatever is due, or nothing.
    /// </summary>
    /// <remarks>
    /// Called on a fresh scope each tick and never re-entered: the runner's loop is
    /// sequential, so a call that outlasts its tick period delays the next one
    /// rather than running beside it.
    /// </remarks>
    Task RunAsync(CancellationToken cancellationToken);
}

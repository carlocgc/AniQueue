using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Asks about relations for titles nobody has asked about yet, with nobody present.
///
/// It gates on its own precondition — titles with
/// no marker, or one older than thirty days — rather than being scheduled, so it
/// converges and then does nothing at all.
/// </summary>
public sealed class RelationBackfillJob(
    IRelationBackfill backfill,
    ILibraryChangeNotifier notifier,
    IOptionsMonitor<TaskOptions> tasks,
    ILogger<RelationBackfillJob> logger) : IBackgroundJob
{
    /// <summary>
    /// How long one visit may keep going, rather than how many requests it may make.
    /// </summary>
    /// <remarks>
    /// <b>The request ceiling is deleted.</b> Sixteen requests covered an 800-title
    /// library in one visit, and a larger one simply finished on the next — which
    /// looked harmless while the tick was fifteen minutes and is the wrong answer
    /// now: pressing <i>Run now</i> and getting "some of it" is exactly the behaviour
    /// the tasks page exists to remove, and a relaxed cadence turns "next visit" into
    /// tomorrow.
    ///
    /// It was never the rate-limit guard either — <see cref="RelationPacing"/> is,
    /// and it is the only thing that decides how fast this goes. What the ceiling
    /// actually protected was tick hygiene, and that does not survive scrutiny: each
    /// job has its own runner, so a long pass delays only the next pass of this job
    /// and cannot block sync or scoring.
    ///
    /// A budget replaces it for the pathological case rather than the ordinary one.
    /// At two seconds a request, ten thousand titles is about seven minutes and a
    /// hundred thousand would be over an hour; this bounds that, and resumption is
    /// free because the marker is per title.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fifteen minutes, and the number barely matters.
    /// </summary>
    /// <remarks>
    /// Polling resolution, not a schedule. What decides whether this does anything is
    /// its own precondition, and a newly synced title does not wait for a tick at all
    /// — the library-change broadcast wakes it.
    /// </remarks>
    public TimeSpan TickPeriod => TimeSpan.FromMinutes(15);

    /// <summary>What this task's runs are filed under. Never shown.</summary>
    public string Key => "relations";

    public string Name => "Related titles";

    /// <summary>
    /// One, and unnamed. There is nothing to divide it by: relations are fetched from
    /// AniList for every title carrying an AniList id, whatever brought the title in.
    /// </summary>
    public IReadOnlyList<JobUnit> Units { get; } = [new JobUnit(null, "Related titles")];

    /// <summary>
    /// Does whatever is outstanding, which is usually nothing.
    /// </summary>
    /// <remarks>
    /// Failures are reported rather than thrown, which is silent degradation
    /// rather than carelessness: a failure here means one row is missing a detail, not
    /// that the library is wrong.
    /// </remarks>
    public async Task<JobRunOutcome> RunAsync(
        JobRunContext context,
        CancellationToken cancellationToken)
    {
        // Nothing gates on a cadence here. Work is "titles nobody has asked about",
        // which is a question the database answers directly, and a job that is a
        // genuine no-op when there is nothing to do needs no schedule to protect
        // anything from it.
        _ = context;

        if (!tasks.CurrentValue.RelationsEnabled)
        {
            // Said out loud, because a switched-off task and a library with no
            // AniList titles both show up as a backlog with no related titles.
            logger.LogDebug("Related titles are switched off; no relations will be fetched");

            return JobRunOutcome.NotDue;
        }

        // The budget is the service's business rather than a token trip, so a pass
        // that runs out of time returns what it managed instead of throwing it away.
        var result = await backfill.RunAsync(Budget, progress: null, cancellationToken);

        if (!result.DidWork)
        {
            return JobRunOutcome.NothingToDo;
        }

        // Published because relations are library data and something downstream may
        // want them — the cover art job wakes on exactly this. Nothing here knows
        // that, which is the point: the signal says data changed, never "run the
        // cover art job".
        //
        // Without a payload, because there is no sentence a page could usefully show
        // about it. A runner wakes on the signal and ignores the detail; the notice
        // that wants detail only ever has it for a sync.
        if (result.ChangedAnything)
        {
            notifier.Publish(origin: Key);
        }

        return result.FailureReason is { } reason
            ? JobRunOutcome.Failed(reason, result.Requested, result.EdgesWritten + result.EdgesRemoved)
            : JobRunOutcome.Succeeded(result.Answered, result.EdgesWritten + result.EdgesRemoved);
    }
}

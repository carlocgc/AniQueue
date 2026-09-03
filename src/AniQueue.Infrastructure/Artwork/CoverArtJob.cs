using AniQueue.Core.Artwork;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Artwork;

/// <summary>
/// Fetches cover art nobody has fetched yet, with nobody present.
///
/// What there is to do is a precondition the database answers directly — rows whose
/// art is not the art they claim — so it converges and then finds nothing. When it is
/// allowed to look is the shared cadence, on the timer only: a library change still
/// brings it forward, and nothing sequences it behind the sync that gives it work.
/// </summary>
public sealed class CoverArtJob(
    IArtworkService artwork,
    ILibraryChangeNotifier notifier,
    IJobRunStore runs,
    IOptionsMonitor<TaskOptions> tasks,
    ILogger<CoverArtJob> logger) : IBackgroundJob
{
    /// <summary>
    /// How long one visit may keep going.
    /// </summary>
    /// <remarks>
    /// A first pass over a fresh 810-title library takes a few minutes at the pacing
    /// the service uses, and two renditions per title mean a large one stops and
    /// resumes instead. That costs nothing, because progress is recorded per picture:
    /// the only visible consequence is a fresh install showing a small poster in the
    /// detail dialog for a while. Raising this would hold a database connection and a
    /// cancellation window open longer to buy nothing.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fifteen minutes, and the number barely matters — the same answer the relation
    /// pass gives, for the same reason. This is polling resolution, and newly synced
    /// titles do not wait for it.
    /// </summary>
    public TimeSpan TickPeriod => TimeSpan.FromMinutes(15);

    /// <summary>What this task's runs are filed under. Never shown.</summary>
    public string Key => "cover-art";

    public string Name => "Cover art";

    /// <summary>
    /// One, and unnamed. Sync has a unit per source because each carries its own
    /// enabled state and failure history; art has neither to divide — a picture is
    /// fetched from wherever the title's row says it is, whatever brought the title
    /// in.
    /// </summary>
    public IReadOnlyList<JobUnit> Units { get; } = [new JobUnit(null, "Cover art")];

    public async Task<JobRunOutcome> RunAsync(JobRunContext context, CancellationToken cancellationToken)
    {
        if (!tasks.CurrentValue.CoverArtEnabled)
        {
            // Said out loud, because a switched-off task and a task with nothing to
            // do are indistinguishable from a page that shows no pictures.
            logger.LogDebug("Cover art is switched off; no pictures will be fetched");

            return JobRunOutcome.NotDue;
        }

        // The cadence gates the timer and nothing else. A library change still brings
        // this forward, which is what stops a title synced at nine o'clock waiting
        // until tomorrow for its picture, and pressing the button is a timed run
        // brought forward by hand.
        //
        // The work here is a question the database answers directly, so a run costs
        // little — but a task whose page says "once a day" and whose row moves every
        // quarter of an hour is reporting a setting it is not keeping.
        if (!context.IgnoresSchedule && !await IsDueAsync(context, cancellationToken))
        {
            return JobRunOutcome.NotDue;
        }

        var result = await artwork.RunAsync(Budget, cancellationToken);

        if (!result.DidWork)
        {
            return JobRunOutcome.NothingToDo;
        }

        // Published because a title gaining art is library data changing, and an open
        // backlog is showing a colour block for a picture that now exists. Nothing
        // here knows who is listening, which is the point.
        if (result.ChangedAnything)
        {
            notifier.Publish(origin: Key);
        }

        // Failures are counted, not raised. A cover that did not arrive is one row
        // missing a detail rather than a library that is wrong, so a pass in which
        // some pictures failed is still a pass that succeeded. Only a pass that could
        // not run at all is a failure.
        return result.FailureReason is { } reason
            ? JobRunOutcome.Failed(reason, result.Considered, result.Fetched)
            : JobRunOutcome.Succeeded(result.Considered, result.Fetched);
    }

    private async Task<bool> IsDueAsync(JobRunContext context, CancellationToken cancellationToken)
    {
        var lastRun = await runs.LastRunAtAsync(Key, context.Unit, cancellationToken);

        if (JobCadence.IsDue(tasks.CurrentValue.Schedule, lastRun, DateTimeOffset.UtcNow))
        {
            return true;
        }

        logger.LogDebug(
            "Cover art is not due: last run {LastRun:u}, cadence {Cadence}",
            lastRun,
            tasks.CurrentValue.Schedule);

        return false;
    }
}

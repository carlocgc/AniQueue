using AniQueue.Core.Artwork;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Artwork;

/// <summary>
/// Fetches cover art nobody has fetched yet, with nobody present.
///
/// D25's third enrichment pass, and the same shape as the second: it gates on its own
/// precondition rather than on a schedule, so it converges and then does nothing at
/// all. Nothing sequences it behind the sync that gives it work — it hears the
/// library changed and looks (D28, D41).
/// </summary>
public sealed class CoverArtJob(
    IArtworkService artwork,
    ILibraryChangeNotifier notifier,
    IOptionsMonitor<TaskOptions> tasks) : IBackgroundJob
{
    /// <summary>
    /// How long one visit may keep going.
    /// </summary>
    /// <remarks>
    /// A first pass over a fresh 810-title library measured four minutes and six
    /// seconds at the pacing the service uses. That was room for a library twice this
    /// one's size to finish in one visit, and Phase 9b spends the headroom: a second
    /// rendition per title at 8.5× the bytes means a first run stops and resumes
    /// instead (D48). It costs nothing because progress is recorded per picture, and
    /// the only visible consequence is that a fresh install shows a small poster in
    /// the detail dialog for a while — the same degradation the colour block exists
    /// for. Raising this would hold a database connection and a cancellation window
    /// open longer to buy nothing.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fifteen minutes, and the number barely matters — the same answer the relation
    /// pass gives, for the same reason. This is polling resolution, and newly synced
    /// titles do not wait for it (D28, D41).
    /// </summary>
    public TimeSpan TickPeriod => TimeSpan.FromMinutes(15);

    /// <summary>What this task's runs are filed under. Never shown.</summary>
    public string Key => "cover-art";

    public string Name => "Cover art";

    /// <summary>
    /// One, and unnamed. Sync has a unit per source because each carries its own
    /// enabled state and failure history; art has neither to divide — a picture is
    /// fetched from wherever the title's row says it is, whatever brought the title
    /// in (D40).
    /// </summary>
    public IReadOnlyList<JobUnit> Units { get; } = [new JobUnit(null, "Cover art")];

    public async Task<JobRunOutcome> RunAsync(JobRunContext context, CancellationToken cancellationToken)
    {
        // Nothing gates on a cadence. What there is to do is "rows whose art is not
        // the art they claim", which the database answers directly, so a job that is
        // a genuine no-op when there is nothing outstanding needs no schedule to
        // protect anything from it (D25).
        _ = context;

        if (!tasks.CurrentValue.CoverArtEnabled)
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
        // here knows who is listening, which is the point (D41).
        if (result.ChangedAnything)
        {
            notifier.Publish(origin: Key);
        }

        // Failures are counted, not raised. A cover that did not arrive is one row
        // missing a detail, and D25 is explicit that this is deliberately unlike a
        // stalled sync — so a pass in which some pictures failed is still a pass that
        // succeeded, and only a pass that could not run at all is a failure.
        return result.FailureReason is { } reason
            ? JobRunOutcome.Failed(reason, result.Considered, result.Fetched)
            : JobRunOutcome.Succeeded(result.Considered, result.Fetched);
    }
}

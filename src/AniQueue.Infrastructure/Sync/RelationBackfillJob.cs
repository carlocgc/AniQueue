using AniQueue.Core.Jobs;
using AniQueue.Core.Sync;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Asks about relations for titles nobody has asked about yet, with nobody present.
///
/// The second job in the runner, and the first instance of D25's shape: it gates on
/// its own precondition — titles with no marker — rather than being scheduled, so it
/// converges once and then does nothing at all. There is no user-facing setting for
/// it, because there is no decision to offer: a relation is a fact about a title,
/// and nobody wants fewer of them.
/// </summary>
public sealed class RelationBackfillJob(IRelationBackfill backfill) : IBackgroundJob
{
    /// <summary>
    /// Sixteen requests, which covers a 750-title library in one visit at the
    /// batch size AniList allows.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a target. Paced at two seconds apart it is about half a
    /// minute of work, which is a reasonable amount of one tick to spend — and a
    /// library large enough to exceed it simply finishes on the next visit, because
    /// the marker makes resumption free.
    /// </remarks>
    private const int MaxRequestsPerVisit = 16;

    /// <summary>
    /// Fifteen minutes, and the number barely matters.
    /// </summary>
    /// <remarks>
    /// Unlike the sync's tick, this is not honouring a schedule anyone chose. It is
    /// how long a newly synced title waits before its relations are known, and the
    /// only thing that observes the difference is a chevron appearing on a row
    /// somebody may not be looking at. Long enough to cost nothing; short enough
    /// that a first run after configuring AniList happens while the user is still
    /// interested.
    /// </remarks>
    public TimeSpan TickPeriod => TimeSpan.FromMinutes(15);

    public string Name => "Relation backfill";

    /// <summary>
    /// Does whatever is outstanding, which is usually nothing.
    /// </summary>
    /// <remarks>
    /// Failures are not rethrown, and that is D25's silent degradation rather than
    /// carelessness: the runner treats a thrown job as a failure and backs its
    /// interval off, which is right for a sync whose failure means the library is
    /// wrong, and wrong for a pass whose failure means one row is missing a detail.
    /// The reason is logged by the service; the next tick tries again at the
    /// ordinary interval.
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken) =>
        backfill.RunAsync(MaxRequestsPerVisit, cancellationToken);
}

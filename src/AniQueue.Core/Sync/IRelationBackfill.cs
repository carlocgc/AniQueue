using AniQueue.Core.Domain;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Sync;

/// <summary>What one pass over the unfetched titles did.</summary>
/// <param name="Requested">Titles asked about.</param>
/// <param name="Answered">Titles the source answered for. Fewer than requested when it dropped some.</param>
/// <param name="EdgesWritten">New edges stored. Zero is the steady state, and also the common one.</param>
/// <param name="EdgesRemoved">
/// Edges the source no longer publishes, for titles it did publish something about.
/// Only ever non-zero on a re-read, which is the reason re-reading is worth doing.
/// </param>
/// <param name="FailureReason">Null when every request completed.</param>
/// <param name="RanOutOfTime">
/// True when the pass stopped because its budget was spent rather than because there
/// was nothing left. Not a failure — the marker makes the rest free to pick up — but
/// the difference between "finished" and "got this far" is worth being able to say.
/// </param>
public sealed record RelationBackfillResult(
    int Requested,
    int Answered,
    int EdgesWritten,
    int EdgesRemoved = 0,
    string? FailureReason = null,
    bool RanOutOfTime = false)
{
    public static RelationBackfillResult Idle { get; } = new(0, 0, 0);

    public bool DidWork => Requested > 0;

    public bool ChangedAnything => EdgesWritten > 0 || EdgesRemoved > 0;
}

/// <summary>
/// How much of the library has been asked about, for the one line the Sources page
/// shows.
/// </summary>
/// <param name="Known">Titles whose relations have been fetched.</param>
/// <param name="Total">Titles that carry an identifier this source could answer for.</param>
public sealed record RelationCoverage(int Known, int Total)
{
    public int Outstanding => Total - Known;

    public bool IsComplete => Outstanding <= 0;

    public static RelationCoverage None { get; } = new(0, 0);
}

/// <summary>
/// Fills in the relation graph for titles nobody has asked about yet.
///
/// Lazy by construction and idle in the steady state: work is "titles carrying an
/// identifier with no <see cref="AnimeExternalId.RelationsFetchedAt"/>", which is
/// everything on the first run and nothing on the second. New titles arrive
/// unmarked and are picked up without anything having to notice they are new.
/// </summary>
/// <remarks>
/// This is the first instance of D25's shape, and the rules it keeps are that
/// decision's rather than this feature's: it gates on its own precondition rather
/// than being told when to run, it is unauthenticated, it only ever adds, and it
/// degrades silently — a failure here means a row is missing a detail, not that the
/// library is wrong, so it logs and waits rather than raising a banner.
/// </remarks>
public interface IRelationBackfill
{
    /// <summary>
    /// Asks about the next titles that have never been asked about.
    /// </summary>
    /// <param name="budget">
    /// How long the pass may keep going. It runs until nothing is outstanding, the
    /// budget is spent, or it is cancelled, and returns what it managed either way.
    /// </param>
    /// <remarks>
    /// <b>A budget rather than a request ceiling</b>, since Phase 15a. The ceiling was
    /// sixteen requests — an 800-title library in one visit, with anything larger
    /// finishing on the next one. That was tolerable while a visit came round every
    /// fifteen minutes and is not now: a manual run that does "some of it" is the
    /// behaviour the tasks page exists to remove, and a relaxed cadence turns "the
    /// next visit" into tomorrow.
    ///
    /// It was never what kept AniQueue inside AniList's rate limit either;
    /// <c>RelationPacing</c> is, and it remains the only thing deciding how fast this
    /// goes. What the ceiling protected was one tick not running long, and each job
    /// has its own runner, so a long pass here delays nothing but itself.
    ///
    /// The budget exists for the pathological case rather than the ordinary one. At
    /// two seconds a request, ten thousand titles is about seven minutes; resumption
    /// is free because the marker is per title, so stopping early costs nothing.
    /// </remarks>
    Task<RelationBackfillResult> RunAsync(
        TimeSpan budget,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads everything now, rather than waiting for it to go stale.
    /// </summary>
    /// <remarks>
    /// The one user-triggered path into any of this, and it exists because the
    /// automatic answer is deliberately slow: a relation added between two titles
    /// the user already owns is invisible until its answer expires, and somebody who
    /// knows a sequel was just announced should not have to wait a month to see it.
    ///
    /// It forgets every marker and asks again, so what it costs is a full pass — the
    /// same fifteen-odd requests the first run made. A library larger than one visit
    /// finishes in the background, which is why this returns what it managed rather
    /// than promising completeness.
    /// </remarks>
    Task<RelationBackfillResult> RefreshAsync(
        TimeSpan budget,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>How much is known, for display. Two counts, both from the database.</summary>
    Task<RelationCoverage> GetCoverageAsync(CancellationToken cancellationToken = default);
}

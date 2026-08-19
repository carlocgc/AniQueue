using AniQueue.Core.Domain;

namespace AniQueue.Core.Sync;

/// <summary>What one pass over the unfetched titles did.</summary>
/// <param name="Requested">Titles asked about.</param>
/// <param name="Answered">Titles the source answered for. Fewer than requested when it dropped some.</param>
/// <param name="EdgesWritten">New edges stored. Zero is the steady state, and also the common one.</param>
/// <param name="FailureReason">Null when every request completed.</param>
public sealed record RelationBackfillResult(
    int Requested,
    int Answered,
    int EdgesWritten,
    string? FailureReason = null)
{
    public static RelationBackfillResult Idle { get; } = new(0, 0, 0);

    public bool DidWork => Requested > 0;
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
    /// <param name="maxRequests">
    /// A ceiling on requests in one visit, so a first run against a large library
    /// spreads over several rather than holding one open for minutes.
    /// </param>
    Task<RelationBackfillResult> RunAsync(int maxRequests, CancellationToken cancellationToken = default);

    /// <summary>How much is known, for display. Two counts, both from the database.</summary>
    Task<RelationCoverage> GetCoverageAsync(CancellationToken cancellationToken = default);
}

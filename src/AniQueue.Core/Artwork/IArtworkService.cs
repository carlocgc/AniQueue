namespace AniQueue.Core.Artwork;

/// <summary>What one visit to the artwork cache did.</summary>
/// <remarks>
/// Failures are counted rather than thrown, because D25 requires enrichment to
/// degrade silently: a cover that did not arrive means one row is missing a detail,
/// not that anything is wrong with the library. <see cref="FailureReason"/> is for a
/// pass that could not run at all, which is a different thing from a pass in which
/// some pictures did not come back.
/// </remarks>
public sealed record ArtworkPassResult
{
    /// <summary>Pictures the pass tried to fetch.</summary>
    public int Considered { get; init; }

    /// <summary>Pictures that arrived and were cached.</summary>
    public int Fetched { get; init; }

    /// <summary>Pictures that did not, for any reason.</summary>
    public int Failed { get; init; }

    /// <summary>
    /// Cached files removed — orphans left by titles that have gone, and by art that
    /// has been replaced.
    /// </summary>
    public int Removed { get; init; }

    /// <summary>Rows whose cached file had vanished and were sent back for a refetch.</summary>
    public int Healed { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>Whether the pass is worth recording as having happened.</summary>
    public bool DidWork => Considered > 0 || Removed > 0 || Healed > 0;

    /// <summary>Whether anything downstream would care (D41).</summary>
    public bool ChangedAnything => Fetched > 0;
}

/// <summary>
/// Fills the artwork cache in, a picture at a time, with nobody watching (D25, D47).
/// </summary>
/// <remarks>
/// The second of D25's enrichment passes to be built, and the same shape as the
/// first: it gates on its own precondition rather than on a schedule, so it converges
/// and then does nothing at all. What counts as outstanding is a row whose
/// <c>FetchedUrl</c> and <c>RemoteUrl</c> disagree — which covers a title that has
/// never had art fetched and one whose art AniList has replaced — or a row whose
/// cached file is no longer on disk.
/// </remarks>
public interface IArtworkService
{
    /// <summary>
    /// Fetches whatever is outstanding, within a time budget.
    /// </summary>
    /// <remarks>
    /// The budget is here rather than expressed as a cancellation token so that a
    /// pass which runs out of time returns what it managed instead of discarding it —
    /// the same choice the relation backfill made, and for the same reason:
    /// resumption is free because progress is recorded per row.
    /// </remarks>
    Task<ArtworkPassResult> RunAsync(TimeSpan budget, CancellationToken cancellationToken);
}

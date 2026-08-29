namespace AniQueue.Core.Artwork;

/// <summary>
/// What one visit to the artwork cache did. Failures are counted rather than
/// thrown: a cover that did not arrive means one row is missing a detail.
/// <see cref="FailureReason"/> is for a pass that could not run at all.
/// </summary>
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

    /// <summary>Whether anything downstream would care.</summary>
    public bool ChangedAnything => Fetched > 0;
}

/// <summary>
/// Fills the artwork cache in, a picture at a time, with nobody watching. It gates
/// on its own precondition rather than on a schedule, so it converges and then does
/// nothing: outstanding work is a row whose <c>FetchedUrl</c> and <c>RemoteUrl</c>
/// disagree, or one whose cached file is no longer on disk.
/// </summary>
public interface IArtworkService
{
    /// <summary>
    /// Fetches whatever is outstanding, within a time budget. A budget rather than a
    /// cancellation token, so a pass that runs out of time returns what it managed
    /// instead of discarding it.
    /// </summary>
    Task<ArtworkPassResult> RunAsync(TimeSpan budget, CancellationToken cancellationToken);
}

namespace AniQueue.Core.Domain;

/// <summary>
/// How the backlog is ordered for display. These are views over the same data —
/// selecting <see cref="Ai"/> never rewrites the manually curated queue.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum RecommendationMode
{
    Manual = 0,
    Ai = 1,
    Hybrid = 2
}

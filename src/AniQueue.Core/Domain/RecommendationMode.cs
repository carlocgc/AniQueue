namespace AniQueue.Core.Domain;

/// <summary>
/// How the backlog is ordered for display. These are three separate *views* over
/// the same data — selecting <see cref="Ai"/> or <see cref="Hybrid"/> never
/// rewrites the manually curated queue (ROADMAP.md §7, Phase 9).
/// </summary>
public enum RecommendationMode
{
    Manual = 0,
    Ai = 1,
    Hybrid = 2
}

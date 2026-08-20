namespace AniQueue.Core.Domain;

/// <summary>
/// How the backlog is ordered for display. These are three separate *views* over
/// the same data — selecting <see cref="Ai"/> or <see cref="Hybrid"/> never
/// rewrites the manually curated queue (ROADMAP.md §7, Phase 7).
/// </summary>
/// <remarks>
/// <see cref="Hybrid"/> is unreachable: the formula that would blend the user's
/// order with the model's was withdrawn by D32 along with the surfaces that would
/// have shown it. The member stays rather than being renumbered, because these
/// values persist in settings and silently changing what a stored number means is
/// how a saved preference becomes a wrong one — the same reason
/// <see cref="Library.LibrarySort"/> left a gap where manual priority was.
/// </remarks>
public enum RecommendationMode
{
    Manual = 0,
    Ai = 1,
    Hybrid = 2
}

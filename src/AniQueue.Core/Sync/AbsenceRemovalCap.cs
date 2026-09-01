namespace AniQueue.Core.Sync;

/// <summary>
/// How much of one source's library a single fetch may delete before AniQueue
/// refuses and holds the absences for the user instead.
/// </summary>
/// <remarks>
/// The guard that makes automatic deletion survivable. A paging bug, a rate limit
/// answered with a short page, a renamed account and a profile turned private all
/// arrive as "most of the list is gone", and that reading is the one mistake here
/// nothing inside the application can undo.
///
/// Proportional with a floor, because neither alone works: a flat number lets a
/// small library be emptied and a bare percentage blocks the ordinary case of one
/// title leaving a library of thirty.
/// </remarks>
public static class AbsenceRemovalCap
{
    /// <summary>Always allowed, however small the library.</summary>
    public const int Floor = 5;

    /// <summary>Allowed as a share of what the source tracks, once that is the larger.</summary>
    public const int Percent = 10;

    /// <summary>The most one fetch may delete for a source tracking <paramref name="tracked"/> titles.</summary>
    public static int For(int tracked) => Math.Max(Floor, tracked * Percent / 100);

    /// <summary>Whether this many absences is too many to act on unasked.</summary>
    public static bool Exceeded(int absent, int tracked) => absent > For(tracked);
}

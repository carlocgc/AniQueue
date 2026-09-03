namespace AniQueue.Core.Domain;

/// <summary>
/// What an unattended sync does with an entry it cannot confidently identify.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncConflictPolicy
{
    /// <summary>Leave it for the user. The default.</summary>
    HoldForReview = 0,

    /// <summary>
    /// Attach the incoming identifier to the same-titled local record. An explicit
    /// opt-in to title-based merging, on exact case-insensitive equality rather
    /// than a similarity heuristic. Writing the identifier is what stops the entry
    /// conflicting again on every subsequent sync.
    /// </summary>
    LinkToExisting = 1
}

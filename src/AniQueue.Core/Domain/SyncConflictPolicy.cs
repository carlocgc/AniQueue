namespace AniQueue.Core.Domain;

/// <summary>
/// What an unattended sync does with an entry it cannot confidently identify (D21).
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncConflictPolicy
{
    /// <summary>
    /// Leave it for the user. The default, because §6 forbids silently merging a
    /// match the application cannot confirm.
    /// </summary>
    HoldForReview = 0,

    /// <summary>
    /// Attach the incoming identifier to the same-titled local record.
    /// </summary>
    /// <remarks>
    /// An explicit opt-in to title-based merging, which §6 otherwise forbids. Two
    /// things make it defensible: the test is exact case-insensitive equality
    /// rather than the similarity heuristic D10 rejected, and it is the only
    /// resolution that converges — writing the identifier is what stops the entry
    /// conflicting again on every subsequent sync.
    ///
    /// There is deliberately no unattended equivalent of "import as new". It
    /// duplicates the row, both copies appear in the backlog, both are queueable,
    /// and no delete-duplicate surface exists.
    /// </remarks>
    LinkToExisting = 1
}

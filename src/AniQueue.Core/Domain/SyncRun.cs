namespace AniQueue.Core.Domain;

/// <summary>
/// One completed attempt to sync one source: the audit trail for writes nobody
/// watched, and the source of the "last synced" the Sources page shows.
///
/// A row is written when a run reaches a terminal state and not before. A failed
/// fetch is recorded immediately, a fetch with nothing to apply is recorded
/// immediately, and a fetch with changes is recorded when they are applied — so a
/// preview the user is still looking at never reads as a completed sync.
/// </summary>
public class SyncRun
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    public AnimeSource Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Null only for a run still in flight, which nothing currently writes.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public SyncOutcome Outcome { get; set; }

    public int Created { get; set; }

    public int Updated { get; set; }

    public int Skipped { get; set; }

    /// <summary>
    /// Conflicts the run did not resolve, which is the pending-decision count the
    /// Sources page badges.
    /// </summary>
    public int ConflictsHeld { get; set; }

    /// <summary>
    /// Queue slots released because the sync showed their titles are no longer
    /// waiting to be watched.
    /// </summary>
    public int SlotsReleased { get; set; }

    /// <summary>
    /// Unambiguous changes an unattended run found and did not apply, because the
    /// source is set to ask first. A count only: what the changes were is not
    /// stored, because the user's visit re-fetches.
    /// </summary>
    public int ChangesHeld { get; set; }

    /// <summary>
    /// Titles this source used to list and no longer does, marked for the user to
    /// look at. Only ever counts rows carrying this source's identifier, and only
    /// when the fetch was structurally complete.
    /// </summary>
    public int AbsentFlagged { get; set; }

    /// <summary>
    /// Why a failed run failed, in plain words. Never a stack trace: this is
    /// rendered to whoever opens the page.
    /// </summary>
    public string? FailureReason { get; set; }
}

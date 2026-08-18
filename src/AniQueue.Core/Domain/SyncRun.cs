namespace AniQueue.Core.Domain;

/// <summary>
/// One completed attempt to sync one source (D21).
///
/// The audit trail for writes nobody watched, and the source of the "last synced"
/// the Sources page shows. It is written in Phase 5b, where every run is
/// user-initiated, because an on-demand run deserves the same record as an
/// unattended one and because a page that cannot say when it last worked is a page
/// that cannot say anything useful about whether it is working.
///
/// <b>A row is written when a run reaches a terminal state, and not before.</b> A
/// fetch that produced a preview the user is still looking at has not finished:
/// nothing has been applied, and recording it as a sync would let the page claim
/// the library is up to date while the changes sit unconfirmed on screen. So a
/// failed fetch is recorded immediately, a fetch with nothing to apply is recorded
/// immediately, and a fetch with changes is recorded when they are applied.
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
    /// Sources page badges (D21).
    /// </summary>
    public int ConflictsHeld { get; set; }

    /// <summary>
    /// Queue slots released because the sync showed their titles are no longer
    /// waiting to be watched (D12).
    /// </summary>
    public int SlotsReleased { get; set; }

    /// <summary>
    /// Unambiguous changes an unattended run found and did not apply, because the
    /// source is set to ask first (D21).
    /// </summary>
    /// <remarks>
    /// A count and nothing else. What the changes were is not stored: a held
    /// preview is stale within the hour and the user's visit re-fetches, so
    /// persisting one would mean showing them a decision computed against a
    /// library that has since moved.
    /// </remarks>
    public int ChangesHeld { get; set; }

    /// <summary>
    /// Titles this source used to list and no longer does, marked for the user to
    /// look at (D19).
    /// </summary>
    /// <remarks>
    /// Only ever counts rows carrying this source's identifier, and only when the
    /// fetch was structurally complete. Under an absence policy of ignore it is
    /// always zero, because nothing is looked for.
    /// </remarks>
    public int AbsentFlagged { get; set; }

    /// <summary>
    /// Why a failed run failed, in plain words. Never a stack trace: this is
    /// rendered to whoever opens the page (§6).
    /// </summary>
    public string? FailureReason { get; set; }
}

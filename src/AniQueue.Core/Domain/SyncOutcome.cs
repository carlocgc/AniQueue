namespace AniQueue.Core.Domain;

/// <summary>
/// How a sync run ended.
///
/// Three values rather than a boolean, because "nothing changed" and "it worked"
/// look identical in a count of zero and mean different things to someone deciding
/// whether their list is up to date. A stalled sync must never render as up to
/// date (§4).
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncOutcome
{
    /// <summary>The fetch succeeded and changes were applied.</summary>
    Succeeded = 0,

    /// <summary>The fetch succeeded and the list already matched the library.</summary>
    NothingToDo = 1,

    /// <summary>
    /// The run did not complete. <see cref="SyncRun.FailureReason"/> says why, in
    /// words meant for the person reading the Sources page.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The fetch worked and found changes, and they were not applied because this
    /// source is set to ask first.
    /// </summary>
    /// <remarks>
    /// A fourth value for the same reason there were three: this is not
    /// <see cref="NothingToDo"/>, and reporting it as such would tell a user their
    /// library matches their list while a dozen changes sit unapplied. It is not
    /// <see cref="Failed"/> either — nothing went wrong, and a red banner for a
    /// setting working as configured teaches people to ignore red banners.
    ///
    /// Nothing about the changes themselves is stored, per D21: a held preview is
    /// stale within the hour, and the user's visit re-fetches and recomputes.
    /// <see cref="SyncRun.ChangesHeld"/> carries the count, which is all the page
    /// needs to say there is something to come and look at.
    /// </remarks>
    HeldForReview = 3
}

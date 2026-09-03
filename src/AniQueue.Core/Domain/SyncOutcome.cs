namespace AniQueue.Core.Domain;

/// <summary>
/// How a sync run ended. Four values rather than a boolean, because "nothing
/// changed", "it worked", "it stalled" and "there is something waiting for you"
/// all look identical in a count of zero and mean different things to someone
/// deciding whether their list is up to date.
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
    /// source is set to ask first. <see cref="SyncRun.ChangesHeld"/> carries the
    /// count; the changes themselves are not stored, because a held preview is
    /// stale within the hour and the user's visit re-fetches.
    /// </summary>
    HeldForReview = 3
}

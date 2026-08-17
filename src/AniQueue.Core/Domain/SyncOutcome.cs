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
    Failed = 2
}

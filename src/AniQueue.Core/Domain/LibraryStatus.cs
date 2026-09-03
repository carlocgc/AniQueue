namespace AniQueue.Core.Domain;

/// <summary>
/// The user's relationship with a title. Stored as an integer, so the numeric
/// values are part of the database contract and must never be reordered or
/// reused — only appended to.
/// </summary>
public enum LibraryStatus
{
    Planning = 0,
    Watching = 1,
    Completed = 2,
    OnHold = 3,
    Dropped = 4
}

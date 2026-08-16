namespace AniQueue.Core.Queue;

/// <summary>
/// A change to a slot's place in the queue, expressed as intent rather than as a
/// destination.
///
/// The buttons say "move up", not "move to position four", and the service
/// resolves that against the queue as it currently stands. That distinction
/// matters because the page the user is looking at can be stale — a second tab, or
/// an earlier click whose render has not landed yet — and an intent stays correct
/// where an absolute position would quietly move the wrong row.
/// </summary>
public enum QueueMove
{
    /// <summary>To the front of the queue.</summary>
    Top,

    /// <summary>One place sooner.</summary>
    Up,

    /// <summary>One place later.</summary>
    Down,

    /// <summary>To the back of the queue.</summary>
    Bottom
}

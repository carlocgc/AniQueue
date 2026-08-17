namespace AniQueue.Core.Queue;

/// <summary>
/// Where a slot lands after a move, and what the queue looks like afterwards.
///
/// This is pure index arithmetic over a list length, deliberately separated from
/// the service that persists the result. D2 gave up the database's help with the
/// contiguity invariant, which makes this arithmetic load-bearing — and having it
/// in Core means it can be exercised exhaustively in milliseconds instead of once
/// per case against SQLite.
///
/// Both entry points return null for a move that changes nothing, so callers can
/// skip the write rather than committing a transaction that rewrites a queue into
/// the order it was already in.
/// </summary>
public static class QueueOrdering
{
    /// <summary>
    /// Resolves a button press against the current queue length.
    /// </summary>
    public static int? TargetIndex(int fromIndex, int count, QueueMove move)
    {
        var requested = move switch
        {
            QueueMove.Top => 0,
            QueueMove.Up => fromIndex - 1,
            QueueMove.Down => fromIndex + 1,
            QueueMove.Bottom => count - 1,
            _ => fromIndex
        };

        return TargetIndex(fromIndex, count, requested);
    }

    /// <summary>
    /// Resolves an explicit destination — what a drag produces.
    /// </summary>
    /// <remarks>
    /// An out-of-range destination is clamped rather than rejected. The index comes
    /// from the browser, so it can legitimately disagree with the server about how
    /// long the queue is; dropping something past the end plainly means "put it
    /// last", and refusing that would fail a gesture the user made correctly.
    /// </remarks>
    public static int? TargetIndex(int fromIndex, int count, int requestedIndex)
    {
        if (count <= 0 || fromIndex < 0 || fromIndex >= count)
        {
            return null;
        }

        var target = Math.Clamp(requestedIndex, 0, count - 1);

        return target == fromIndex ? null : target;
    }

    /// <summary>
    /// Moves one element, shifting everything between the two positions along.
    /// </summary>
    /// <remarks>
    /// The result is always a permutation of the input — nothing is dropped or
    /// duplicated — which is what lets the caller rewrite positions as 0..n-1 from
    /// the new order and know the queue is still contiguous.
    /// </remarks>
    public static List<T> Move<T>(IReadOnlyList<T> items, int fromIndex, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, items.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, items.Count);

        var reordered = new List<T>(items);
        var moved = reordered[fromIndex];

        reordered.RemoveAt(fromIndex);
        reordered.Insert(toIndex, moved);

        return reordered;
    }
}

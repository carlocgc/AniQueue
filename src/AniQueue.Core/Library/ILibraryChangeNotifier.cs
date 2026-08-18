using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>What a background write did, in the terms a page needs to describe it.</summary>
public sealed record LibraryChange
{
    public required AnimeSource Source { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    /// <summary>Queue slots released because their titles are no longer waiting (D12).</summary>
    public int SlotsReleased { get; init; }

    /// <summary>Titles the source has stopped listing (D19).</summary>
    public int AbsentFlagged { get; init; }
}

/// <summary>
/// Tells open pages that something changed underneath them.
///
/// Blazor Server re-renders only when something calls <c>StateHasChanged</c>, and
/// a background write in its own scope cannot reach an open circuit — so a page
/// left open during an unattended sync shows yesterday's library indefinitely and
/// has no way to know. This is the channel that says otherwise.
///
/// <b>It carries a fact, not a command.</b> Subscribers are expected to offer a
/// refresh rather than reload themselves: a page that rearranges under the cursor
/// while somebody is reading it is worse than a stale one, and Phase 4 made
/// staleness safe on purpose — every queue mutation resolves against the database
/// inside its transaction and keys on <c>QueueItemId</c>, so acting on a stale Up
/// Next either does the right thing or returns false and logs.
/// </summary>
public interface ILibraryChangeNotifier
{
    /// <summary>
    /// Raised after a background write. Handlers run on whatever thread published
    /// the change, which is never the render thread — a component subscribing must
    /// marshal with <c>InvokeAsync</c> before touching its own state, and must
    /// unsubscribe when it is disposed. A singleton holding a reference to a
    /// disposed component is a leak that survives every navigation.
    /// </summary>
    event Action<LibraryChange>? Changed;

    void Publish(LibraryChange change);
}

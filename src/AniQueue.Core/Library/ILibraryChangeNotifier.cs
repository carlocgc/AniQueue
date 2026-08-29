using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>What a background write did, in the terms a page needs to describe it.</summary>
public sealed record LibraryChange
{
    public required AnimeSource Source { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    /// <summary>Queue slots released because their titles are no longer waiting.</summary>
    public int SlotsReleased { get; init; }

    /// <summary>Titles the source has stopped listing.</summary>
    public int AbsentFlagged { get; init; }
}

/// <summary>
/// One announcement: what changed, and who says so.
/// </summary>
/// <param name="Change">
/// What a page could render, or null where the publisher has nothing to say in words.
/// </param>
/// <param name="Origin">
/// The key of the job that published it, or null when a page did.
/// </param>
/// <remarks>
/// Origin exists so that a job never wakes itself. A job that changes something on
/// nearly every run would otherwise wake its own runner on nearly every run, costing
/// a wasted pass and a history row saying a task ran for no reason.
/// </remarks>
public sealed record LibraryChangeNotification(LibraryChange? Change, string? Origin);

/// <summary>
/// Tells open pages that something changed underneath them.
///
/// Blazor Server re-renders only when something calls <c>StateHasChanged</c>, and
/// a background write in its own scope cannot reach an open circuit — so a page
/// left open during an unattended sync shows yesterday's library indefinitely and
/// has no way to know. This is the channel that says otherwise.
///
/// It carries a fact, not a command. Subscribers offer a refresh rather than
/// reloading themselves: a page that rearranges under the cursor is worse than a
/// stale one, and staleness is safe here because every queue mutation resolves
/// against the database inside its own transaction.
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
    event Action<LibraryChangeNotification>? Changed;

    /// <summary>
    /// Says something changed, with as much detail as the publisher has.
    /// </summary>
    /// <remarks>
    /// Null is a real argument, not a missing one. The two audiences want different
    /// things: a page wants a sentence it can show, while a runner wants only "go and
    /// check your precondition" and discards the payload. A job with nothing a page
    /// could render says so by passing nothing, the notice stays quiet, and every
    /// runner still wakes.
    /// </remarks>
    /// <param name="origin">
    /// The publishing job's key, so its own runner can ignore it. Null from a page,
    /// which no runner is.
    /// </param>
    void Publish(LibraryChange? change = null, string? origin = null);
}

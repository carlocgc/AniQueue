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
/// One announcement: what changed, and who says so.
/// </summary>
/// <param name="Change">
/// What a page could render, or null where the publisher has nothing to say in words.
/// </param>
/// <param name="Origin">
/// The key of the job that published it, or null when a page did.
/// </param>
/// <remarks>
/// <b>Origin exists so that a job never wakes itself.</b> D41 said every job announces
/// what it changed and warned that publishing carelessly makes jobs wake each other in
/// a ring; it left that as a discipline — publish only when something actually changed
/// — and the discipline is not enough. A job that changes something on nearly every
/// run wakes its own runner on nearly every run, which is exactly what the relation
/// backfill did: a manual pass that wrote 826 edges immediately produced a second run
/// triggered by "the library changed", finding nothing.
///
/// It terminated, because the second pass had nothing to do. That is the whole of what
/// the discipline bought, and it is not worth the wasted pass or the history row that
/// says a task ran for no reason. Nothing is served by a job hearing its own news, so
/// the runner discards it.
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
    event Action<LibraryChangeNotification>? Changed;

    /// <summary>
    /// Says something changed, with as much detail as the publisher has.
    /// </summary>
    /// <remarks>
    /// <b>Null is a real argument, not a missing one.</b> D41 makes every job announce
    /// what it changed, and the two audiences want different things from that: a page
    /// wants a sentence it can show, while a runner wants only "go and check your
    /// precondition" — which is why <c>BackgroundJobRunner</c> has always discarded
    /// the payload outright.
    ///
    /// Generalising <see cref="LibraryChange"/> to describe relations and scoring was
    /// the obvious alternative and it is the wrong shape: it would make every job
    /// invent counts for the benefit of a listener that ignores them, and force
    /// <c>StaleLibraryNotice</c> to grow a sentence for each new kind of work. So a
    /// job with nothing a page could usefully render says so by passing nothing, the
    /// notice stays quiet, and every runner still wakes.
    ///
    /// This does not weaken D41's rule. The signal is still "data changed" and never
    /// "run X next"; what varies is how much the publisher can say about it.
    /// </remarks>
    /// <param name="origin">
    /// The publishing job's key, so its own runner can ignore it. Null from a page,
    /// which no runner is.
    /// </param>
    void Publish(LibraryChange? change = null, string? origin = null);
}

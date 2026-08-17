using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;
using AniQueue.Core.Queue;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Queue;

/// <summary>
/// The Up Next queue.
///
/// Every mutation follows the same shape: open a transaction, load the profile's
/// slots in order, change the order, then rewrite <see cref="QueueItem.Position"/>
/// from the resulting sequence. Rewriting rather than patching is what keeps
/// positions contiguous without a unique index to lean on (D2), and it means a
/// queue that somehow arrived with gaps or duplicates is repaired by the next
/// ordinary edit rather than needing a separate fixer.
///
/// The rewrite looks wasteful and is not: EF issues UPDATEs only for rows whose
/// position actually changed, so moving one row up costs two, and the whole table
/// is only ever touched by a move to the very top or bottom of a long queue.
/// </summary>
public sealed class QueueService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ILogger<QueueService> logger) : IQueueService
{
    public async Task<IReadOnlyList<QueueListItem>> GetQueueAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // One query, because a slot is one title (D15). Under the old model this
        // needed three: the slots, then titles and franchises resolved separately
        // and stitched back together in memory.
        var rows = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId)
            .OrderBy(q => q.Position)
            .ThenBy(q => q.Id)
            .Select(q => new
            {
                q.Id,
                q.Position,
                q.AnimeId,
                q.Anime!.Title,
                q.Anime.MediaType,
                q.Anime.EpisodeCount,
                q.Anime.EpisodeDurationMinutes,
                q.Anime.ReleaseYear,
                q.Anime.Source,
                q.Anime.SourceAnimeId,
                FranchiseName = q.Anime.Franchise != null ? q.Anime.Franchise.Name : null,
                Entry = context.LibraryEntries
                    .Where(e => e.ProfileId == profileId && e.AnimeId == q.AnimeId)
                    .Select(e => new { e.Status, e.EpisodesWatched })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(r => new QueueListItem
        {
            QueueItemId = r.Id,
            Position = r.Position,
            AnimeId = r.AnimeId,
            Title = r.Title,
            MediaType = r.MediaType,
            EpisodeCount = r.EpisodeCount,
            ReleaseYear = r.ReleaseYear,
            Status = r.Entry?.Status,
            EpisodesWatched = r.Entry?.EpisodesWatched ?? 0,
            Source = r.Source,
            SourceAnimeId = r.SourceAnimeId,
            FranchiseName = r.FranchiseName,
            EstimatedRuntimeMinutes = RuntimeCalculator.Estimate(r.EpisodeCount, r.EpisodeDurationMinutes)
        });
    }

    public async Task<QueueAddResult> AddAnimeAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(animeIds);

        if (animeIds.Count == 0)
        {
            return new QueueAddResult(0, 0);
        }

        const string Message = "Adding to Up Next";
        progress?.Report(new OperationProgress(Message));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Read inside the transaction. The unique index would reject a duplicate
        // anyway, but failing the whole batch because one title was already queued
        // would be a poor trade for the user.
        var slots = await LoadOrderedAsync(context, profileId, cancellationToken);
        var queued = slots.Select(q => q.AnimeId).ToHashSet();

        // Distinct so a selection containing the same id twice cannot violate the
        // unique index within one batch.
        var requested = animeIds.Distinct().ToList();
        var toAdd = requested.Where(id => !queued.Contains(id)).ToList();

        // Only titles that actually exist; a stale selection must not create a slot
        // pointing at nothing.
        var existingIds = (await context.Anime
                .Where(a => toAdd.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var position = slots.Count;
        var added = 0;

        // Caller order is preserved, which is what lets a franchise be queued in
        // viewing order by passing its members already sorted.
        foreach (var animeId in toAdd.Where(existingIds.Contains))
        {
            context.QueueItems.Add(new QueueItem
            {
                ProfileId = profileId,
                Position = position++,
                AnimeId = animeId,
                AddedAt = now
            });

            added++;
            progress?.Report(new OperationProgress(Message, added, toAdd.Count));
        }

        // Existing slots are renumbered as well, so an append also repairs a queue
        // that was already non-contiguous. Counting new rows from slots.Count above
        // is only correct because of this.
        RewritePositions(slots);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var skipped = requested.Count - added;

        logger.LogInformation(
            "Queue changed: {Added} added, {Skipped} skipped as already queued or missing",
            added,
            skipped);

        return new QueueAddResult(added, skipped);
    }

    public async Task<QueueAddResult> AddFranchiseAsync(
        int profileId,
        int franchiseId,
        bool includeOptional = false,
        CancellationToken cancellationToken = default)
    {
        List<int> members;

        // Read in its own context, which is closed before the append opens a
        // transaction of its own. Anything that changes in between is caught by the
        // append re-checking inside that transaction; the worst case is that fewer
        // titles land than were counted, which is the safe direction.
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            members = await QueueableMembers(context, profileId, franchiseId, includeOptional)
                .ToListAsync(cancellationToken);
        }

        if (members.Count == 0)
        {
            return new QueueAddResult(0, 0);
        }

        logger.LogInformation(
            "Expanding franchise {FranchiseId} into {Count} queue slots",
            franchiseId,
            members.Count);

        // Handed to the ordinary append in viewing order. Expansion deliberately
        // owns no writing of its own — one path creates slots, so the contiguity
        // invariant has one place to be got right (D15).
        return await AddAnimeAsync(profileId, members, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlySet<int>> GetQueuedAnimeIdsAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ids = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId)
            .Select(q => q.AnimeId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<QueueableFranchise>> GetQueueableFranchisesAsync(
        int profileId,
        bool includeOptional = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var franchises = await context.Franchises
            .AsNoTracking()
            .OrderBy(f => f.Name)
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);

        var offers = new List<QueueableFranchise>();

        foreach (var franchise in franchises)
        {
            // Counted the same way the click will expand it, so the number offered
            // is the number that will land. A franchise that is fully watched or
            // fully queued counts zero and is not offered at all — under the old
            // model it would have been, and then done nothing.
            var count = await QueueableMembers(context, profileId, franchise.Id, includeOptional)
                .CountAsync(cancellationToken);

            if (count > 0)
            {
                offers.Add(new QueueableFranchise(franchise.Id, franchise.Name, count));
            }
        }

        return offers;
    }

    public async Task<bool> RemoveAsync(
        int profileId,
        int queueItemId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var slots = await LoadOrderedAsync(context, profileId, cancellationToken);
        var slot = slots.Find(q => q.Id == queueItemId);

        if (slot is null)
        {
            logger.LogWarning(
                "Queue removal ignored: slot {QueueItemId} is not in profile {ProfileId}'s queue",
                queueItemId,
                profileId);

            return false;
        }

        slots.Remove(slot);
        context.QueueItems.Remove(slot);

        // Closing the gap is the point: leaving position 4 empty would leave the
        // next reorder computing indices against a sequence that has a hole in it.
        var rewritten = RewritePositions(slots);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Queue changed: slot {QueueItemId} removed, {Rewritten} positions closed up",
            queueItemId,
            rewritten);

        return true;
    }

    public async Task<int> AdvanceAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var slots = await LoadOrderedAsync(context, profileId, cancellationToken);

        if (slots.Count == 0)
        {
            return 0;
        }

        var queuedIds = slots.ConvertAll(s => s.AnimeId);

        // Statuses for the queued titles and nothing else. Narrowed to those ids
        // rather than reading the profile's whole library: this runs after every
        // import, and the queue is small where the library is not.
        //
        // A status exists only where a library entry does, and that absence is
        // load-bearing below — it keeps "not planned" distinct from "nothing is
        // known", which are very different grounds for discarding a slot.
        var statuses = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && queuedIds.Contains(e.AnimeId))
            .ToDictionaryAsync(e => e.AnimeId, e => e.Status, cancellationToken);

        // Released only on positive evidence of being done. A queued title with no
        // library entry is unknown, and unknown is not watched.
        var released = slots
            .Where(s => statuses.TryGetValue(s.AnimeId, out var status) && status != LibraryStatus.Planning)
            .ToList();

        if (released.Count == 0)
        {
            return 0;
        }

        foreach (var slot in released)
        {
            slots.Remove(slot);
            context.QueueItems.Remove(slot);
        }

        RewritePositions(slots);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Queue advanced: {Released} slots released because their titles are no longer planned, {Remaining} remaining",
            released.Count,
            slots.Count);

        return released.Count;
    }

    public Task<bool> MoveAsync(
        int profileId,
        int queueItemId,
        QueueMove move,
        CancellationToken cancellationToken = default) =>
        ReorderInternalAsync(
            profileId,
            queueItemId,
            (fromIndex, count) => QueueOrdering.TargetIndex(fromIndex, count, move),
            cancellationToken);

    public Task<bool> ReorderAsync(
        int profileId,
        int queueItemId,
        int targetPosition,
        CancellationToken cancellationToken = default) =>
        ReorderInternalAsync(
            profileId,
            queueItemId,
            (fromIndex, count) => QueueOrdering.TargetIndex(fromIndex, count, targetPosition),
            cancellationToken);

    /// <summary>
    /// The single write path for every reorder. <paramref name="resolveTarget"/>
    /// receives the slot's current index and the queue length, and returns where it
    /// should end up — or null when the request changes nothing.
    /// </summary>
    private async Task<bool> ReorderInternalAsync(
        int profileId,
        int queueItemId,
        Func<int, int, int?> resolveTarget,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var slots = await LoadOrderedAsync(context, profileId, cancellationToken);
        var fromIndex = slots.FindIndex(q => q.Id == queueItemId);

        if (fromIndex < 0)
        {
            logger.LogWarning(
                "Queue reorder ignored: slot {QueueItemId} is not in profile {ProfileId}'s queue",
                queueItemId,
                profileId);

            return false;
        }

        // The index is resolved here, inside the transaction, against the order the
        // database actually holds — not against whatever the page was rendered
        // from. A stale page is the normal case, not an edge one.
        if (resolveTarget(fromIndex, slots.Count) is not { } toIndex)
        {
            return false;
        }

        var rewritten = RewritePositions(QueueOrdering.Move(slots, fromIndex, toIndex));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Queue changed: slot {QueueItemId} moved from {FromIndex} to {ToIndex}, {Rewritten} positions rewritten",
            queueItemId,
            fromIndex,
            toIndex,
            rewritten);

        return true;
    }

    /// <summary>
    /// The members of a franchise that queueing it would actually add, in viewing
    /// order. Defined once so the count offered and the set appended cannot drift.
    /// </summary>
    private static IQueryable<int> QueueableMembers(
        AniQueueDbContext context,
        int profileId,
        int franchiseId,
        bool includeOptional)
    {
        var members = context.Anime
            .AsNoTracking()
            .Where(a => a.FranchiseId == franchiseId);

        if (!includeOptional)
        {
            // Specials and side films the user has marked skippable. They can still
            // be queued individually from the backlog; what they do not do is arrive
            // uninvited when someone says "I want to watch this franchise".
            members = members.Where(a => !a.OptionalWithinFranchise);
        }

        return members
            // Still waiting to be watched, and not already in the queue. Together
            // these make re-adding a franchise after a new season syncs add exactly
            // the new season.
            .Where(a => context.LibraryEntries.Any(e =>
                e.ProfileId == profileId
                && e.AnimeId == a.Id
                && e.Status == LibraryStatus.Planning))
            .Where(a => !context.QueueItems.Any(q => q.ProfileId == profileId && q.AnimeId == a.Id))
            // Unsequenced members last rather than first: a null order means nobody
            // has said where it goes, which is not a claim that it goes before the
            // first season.
            .OrderBy(a => a.FranchiseOrder == null)
            .ThenBy(a => a.FranchiseOrder)
            .ThenBy(a => a.Title)
            .Select(a => a.Id);
    }

    /// <summary>
    /// The profile's slots as tracked entities, in queue order.
    /// </summary>
    /// <remarks>
    /// The <c>Id</c> tiebreak is not decoration. Position is not unique in the
    /// schema (D2), so two rows can in principle share one — after a crashed write,
    /// say — and without a tiebreak SQLite is free to return them in either order,
    /// which would make a reorder land somewhere different each time it ran.
    /// </remarks>
    private static Task<List<QueueItem>> LoadOrderedAsync(
        AniQueueDbContext context,
        int profileId,
        CancellationToken cancellationToken) =>
        context.QueueItems
            .Where(q => q.ProfileId == profileId)
            .OrderBy(q => q.Position)
            .ThenBy(q => q.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Numbers the given order 0..n-1 on tracked entities.
    /// </summary>
    /// <returns>How many rows actually changed, which is what will be written.</returns>
    private static int RewritePositions(List<QueueItem> ordered)
    {
        var changed = 0;

        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Position == index)
            {
                continue;
            }

            ordered[index].Position = index;
            changed++;
        }

        return changed;
    }
}

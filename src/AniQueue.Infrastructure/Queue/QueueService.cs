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
        // needed three: the slots, then titles and groupings resolved separately
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
                q.Anime.CoverImageColor,

                // Scalar subqueries rather than a join, as the backlog does: a slot
                // wants one poster and a join would multiply the queue by however
                // many images a title has (D47, D48).
                CoverContentHash = q.Anime.Images
                    .Where(x => x.Kind == ImageKind.Poster && x.ContentHash != null)
                    .Select(x => x.ContentHash)
                    .FirstOrDefault(),
                CoverFileExtension = q.Anime.Images
                    .Where(x => x.Kind == ImageKind.Poster && x.ContentHash != null)
                    .Select(x => x.FileExtension)
                    .FirstOrDefault(),

                ExternalIds = q.Anime.ExternalIds
                    .Select(x => new { x.Source, x.ExternalId })
                    .ToList(),
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
            ExternalIds = [.. r.ExternalIds.Select(x => new ExternalIdentifier(x.Source, x.ExternalId))],
            EstimatedRuntimeMinutes = RuntimeCalculator.Estimate(r.EpisodeCount, r.EpisodeDurationMinutes),
            CoverContentHash = r.CoverContentHash,
            CoverFileExtension = r.CoverFileExtension,
            CoverImageColor = r.CoverImageColor
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
            return new QueueAddResult { Added = 0 };
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
        var alreadyQueued = requested.Count(queued.Contains);
        var toAdd = requested.Where(id => !queued.Contains(id)).ToList();

        // Statuses decide what may be queued at all. Read from the library rather
        // than the catalogue: a title with no entry for this profile has nothing to
        // plan, and a LibraryEntry cannot exist without its Anime, so this also
        // covers the stale-selection case that used to need its own query.
        var statuses = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && toAdd.Contains(e.AnimeId))
            .ToDictionaryAsync(e => e.AnimeId, e => e.Status, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var position = slots.Count;
        var added = 0;
        var noLongerPlanned = 0;
        var unavailable = 0;

        // Caller order is preserved, which is what lets a run of seasons be queued
        // in viewing order by passing them already sorted.
        foreach (var animeId in toAdd)
        {
            if (!statuses.TryGetValue(animeId, out var status))
            {
                unavailable++;
                continue;
            }

            // The same rule AdvanceAsync applies later. Enforcing it here too is
            // what stops a watched title being queued into a slot that the next
            // import would silently delete.
            if (status != LibraryStatus.Planning)
            {
                noLongerPlanned++;
                continue;
            }

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

        var result = new QueueAddResult
        {
            Added = added,
            AlreadyQueued = alreadyQueued,
            NoLongerPlanned = noLongerPlanned,
            Unavailable = unavailable
        };

        logger.LogInformation(
            "Queue changed: {Added} added, {Skipped} skipped ({AlreadyQueued} already queued, "
            + "{NoLongerPlanned} no longer planned, {Unavailable} not in the library)",
            added,
            result.Skipped,
            alreadyQueued,
            noLongerPlanned,
            unavailable);

        return result;
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

    public Task<bool> RemoveAsync(
        int profileId,
        int queueItemId,
        CancellationToken cancellationToken = default) =>
        RemoveSlotAsync(profileId, q => q.Id == queueItemId, "slot", queueItemId, cancellationToken);

    public Task<bool> RemoveAnimeAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default) =>
        RemoveSlotAsync(profileId, q => q.AnimeId == animeId, "title", animeId, cancellationToken);

    /// <summary>
    /// Takes out whichever slot matches and closes the gap it leaves.
    /// </summary>
    /// <remarks>
    /// Shared by both removals because the difference between them is only how the
    /// slot is named — by its own id from the queue page, by the title it holds from
    /// the backlog, which never sees a slot id. Everything after the lookup is the
    /// invariant, and the invariant is the part that must not exist twice.
    /// </remarks>
    private async Task<bool> RemoveSlotAsync(
        int profileId,
        Predicate<QueueItem> match,
        string lookupKind,
        int identifier,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var slots = await LoadOrderedAsync(context, profileId, cancellationToken);
        var slot = slots.Find(match);

        if (slot is null)
        {
            // One constant template with the lookup as a value, rather than a
            // template built per caller: a message that varies between calls cannot
            // be grouped by anything reading structured logs.
            logger.LogWarning(
                "Queue removal ignored: no slot matched {LookupKind} {Identifier} in profile {ProfileId}'s queue",
                lookupKind,
                identifier,
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
            slot.Id,
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

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

        var slots = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId)
            .OrderBy(q => q.Position)
            .ThenBy(q => q.Id)
            .Select(q => new { q.Id, q.Position, q.AnimeId, q.FranchiseId })
            .ToListAsync(cancellationToken);

        if (slots.Count == 0)
        {
            return [];
        }

        var animeIds = slots.Where(s => s.AnimeId != null).Select(s => s.AnimeId!.Value).ToList();
        var franchiseIds = slots.Where(s => s.FranchiseId != null).Select(s => s.FranchiseId!.Value).ToList();

        var titles = await LoadTitleSlotsAsync(context, profileId, animeIds, cancellationToken);
        var franchises = await LoadFranchiseSlotsAsync(context, profileId, franchiseIds, cancellationToken);

        var items = new List<QueueListItem>(slots.Count);

        foreach (var slot in slots)
        {
            // A slot whose target vanished should not exist — both foreign keys
            // cascade — but rendering the rest of the queue beats throwing away the
            // page over one impossible row.
            QueueListItem? item = slot switch
            {
                { AnimeId: { } animeId } => titles.GetValueOrDefault(animeId) is { } title
                    ? title with { QueueItemId = slot.Id, Position = slot.Position }
                    : null,
                { FranchiseId: { } franchiseId } => franchises.GetValueOrDefault(franchiseId) is { } franchise
                    ? franchise with { QueueItemId = slot.Id, Position = slot.Position }
                    : null,
                _ => null
            };

            if (item is null)
            {
                logger.LogWarning(
                    "Queue slot {QueueItemId} references something that no longer exists; skipping it",
                    slot.Id);

                continue;
            }

            items.Add(item);
        }

        return items;
    }

    public Task<QueueAddResult> AddAnimeAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        AppendAsync(profileId, animeIds, asFranchise: false, progress, cancellationToken);

    public Task<QueueAddResult> AddFranchisesAsync(
        int profileId,
        IReadOnlyCollection<int> franchiseIds,
        CancellationToken cancellationToken = default) =>
        AppendAsync(profileId, franchiseIds, asFranchise: true, progress: null, cancellationToken);

    public async Task<IReadOnlySet<int>> GetQueuedAnimeIdsAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ids = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId && q.AnimeId != null)
            .Select(q => q.AnimeId!.Value)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<QueueableFranchise>> GetQueueableFranchisesAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var queued = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId && q.FranchiseId != null)
            .Select(q => q.FranchiseId!.Value)
            .ToListAsync(cancellationToken);

        // Empty franchises are excluded rather than offered and then queued as a
        // slot with nothing behind it.
        var candidates = await context.Franchises
            .AsNoTracking()
            .Where(f => !queued.Contains(f.Id) && f.Entries.Count > 0)
            .OrderBy(f => f.Name)
            .Select(f => new QueueableFranchise(f.Id, f.Name, f.Entries.Count))
            .ToListAsync(cancellationToken);

        return candidates;
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
    /// Appends anime or franchises to the end of the queue.
    /// </summary>
    /// <remarks>
    /// One method for both because everything except which column is set and which
    /// table is checked for existence is identical, and the part worth getting right
    /// — one transaction, skip duplicates, leave positions contiguous — should exist
    /// once.
    /// </remarks>
    private async Task<QueueAddResult> AppendAsync(
        int profileId,
        IReadOnlyCollection<int> ids,
        bool asFranchise,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return new QueueAddResult(0, 0);
        }

        var message = asFranchise ? "Adding franchises to Up Next" : "Adding to Up Next";
        progress?.Report(new OperationProgress(message));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Read inside the transaction. The filtered unique index would reject a
        // duplicate anyway, but failing the whole batch because one title was
        // already queued would be a poor trade for the user.
        var slots = await LoadOrderedAsync(context, profileId, cancellationToken);

        var queued = slots
            .Select(q => asFranchise ? q.FranchiseId : q.AnimeId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        // Distinct so a selection containing the same id twice cannot violate the
        // unique index within one batch.
        var requested = ids.Distinct().ToList();
        var toAdd = requested.Where(id => !queued.Contains(id)).ToList();

        // Only things that actually exist; a stale selection must not create a slot
        // pointing at nothing.
        var existing = asFranchise
            ? await context.Franchises
                .Where(f => toAdd.Contains(f.Id))
                .Select(f => f.Id)
                .ToListAsync(cancellationToken)
            : await context.Anime
                .Where(a => toAdd.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(cancellationToken);

        var existingIds = existing.ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var position = slots.Count;
        var added = 0;

        foreach (var id in toAdd.Where(existingIds.Contains))
        {
            context.QueueItems.Add(new QueueItem
            {
                ProfileId = profileId,
                Position = position++,
                AnimeId = asFranchise ? null : id,
                FranchiseId = asFranchise ? id : null,
                AddedAt = now
            });

            added++;
            progress?.Report(new OperationProgress(message, added, toAdd.Count));
        }

        // Existing slots are renumbered as well, so an append also repairs a queue
        // that was already non-contiguous. New rows counted from slots.Count above
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

    private static async Task<Dictionary<int, QueueListItem>> LoadTitleSlotsAsync(
        AniQueueDbContext context,
        int profileId,
        List<int> animeIds,
        CancellationToken cancellationToken)
    {
        if (animeIds.Count == 0)
        {
            return [];
        }

        // Left-joined onto the library rather than inner-joined: a queued title with
        // no library entry is odd but not impossible, and it should still render
        // with an unknown status instead of disappearing from the queue.
        var rows = await context.Anime
            .AsNoTracking()
            .Where(a => animeIds.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.MediaType,
                a.EpisodeCount,
                a.EpisodeDurationMinutes,
                a.ReleaseYear,
                a.Source,
                a.SourceAnimeId,
                Entry = context.LibraryEntries
                    .Where(e => e.ProfileId == profileId && e.AnimeId == a.Id)
                    .Select(e => new { e.Status, e.EpisodesWatched })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            r => r.Id,
            r => new QueueListItem
            {
                // Overwritten per slot by the caller; a title can only be queued
                // once, but the record is keyed by anime here, not by slot.
                QueueItemId = 0,
                Position = 0,
                Title = r.Title,
                AnimeId = r.Id,
                MediaType = r.MediaType,
                EpisodeCount = r.EpisodeCount,
                ReleaseYear = r.ReleaseYear,
                Status = r.Entry?.Status,
                EpisodesWatched = r.Entry?.EpisodesWatched ?? 0,
                Source = r.Source,
                SourceAnimeId = r.SourceAnimeId,
                EstimatedRuntimeMinutes = RuntimeCalculator.Estimate(r.EpisodeCount, r.EpisodeDurationMinutes)
            });
    }

    private static async Task<Dictionary<int, QueueListItem>> LoadFranchiseSlotsAsync(
        AniQueueDbContext context,
        int profileId,
        List<int> franchiseIds,
        CancellationToken cancellationToken)
    {
        if (franchiseIds.Count == 0)
        {
            return [];
        }

        var franchises = await context.Franchises
            .AsNoTracking()
            .Where(f => franchiseIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);

        // Members are loaded rather than aggregated in SQL because the runtime sum
        // has to report whether it is partial (§7, Phase 5), and "how many of these
        // had an unknown length" is not something a SUM can tell us. The set is
        // bounded by what is actually queued, so it stays small.
        var members = await context.Anime
            .AsNoTracking()
            .Where(a => a.FranchiseId != null && franchiseIds.Contains(a.FranchiseId.Value))
            .Select(a => new
            {
                FranchiseId = a.FranchiseId!.Value,
                a.EpisodeCount,
                a.EpisodeDurationMinutes,
                IsCompleted = context.LibraryEntries.Any(e =>
                    e.ProfileId == profileId
                    && e.AnimeId == a.Id
                    && e.Status == LibraryStatus.Completed)
            })
            .ToListAsync(cancellationToken);

        var byFranchise = members.ToLookup(m => m.FranchiseId);

        return franchises.ToDictionary(
            f => f.Id,
            f =>
            {
                var entries = byFranchise[f.Id].ToList();

                var (minutes, isPartial) = RuntimeCalculator.Sum(
                    entries.Select(e => RuntimeCalculator.Estimate(e.EpisodeCount, e.EpisodeDurationMinutes)));

                return new QueueListItem
                {
                    QueueItemId = 0,
                    Position = 0,
                    Title = f.Name,
                    FranchiseId = f.Id,
                    EntryCount = entries.Count,
                    CompletedEntryCount = entries.Count(e => e.IsCompleted),
                    EstimatedRuntimeMinutes = minutes,
                    IsRuntimePartial = isPartial
                };
            });
    }
}

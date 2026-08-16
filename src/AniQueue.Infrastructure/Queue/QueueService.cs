using AniQueue.Core.Domain;
using AniQueue.Core.Progress;
using AniQueue.Core.Queue;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Queue;

/// <summary>
/// The Up Next queue. Phase 3 needs only additions from the backlog; reordering
/// and the move buttons arrive with Phase 4.
/// </summary>
public sealed class QueueService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ILogger<QueueService> logger) : IQueueService
{
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

        progress?.Report(new OperationProgress("Adding to Up Next"));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Read inside the transaction. The filtered unique index would reject a
        // duplicate anyway, but failing the whole batch because one title was
        // already queued would be a poor trade for the user.
        var alreadyQueued = await context.QueueItems
            .Where(q => q.ProfileId == profileId && q.AnimeId != null)
            .Select(q => q.AnimeId!.Value)
            .ToListAsync(cancellationToken);

        var queued = alreadyQueued.ToHashSet();

        // Appended after whatever is already there. Positions stay contiguous
        // because this is the only writer and it starts from the current maximum
        // (D2 — the database does not defend contiguity, the service does).
        var nextPosition = await context.QueueItems
            .Where(q => q.ProfileId == profileId)
            .Select(q => (int?)q.Position)
            .MaxAsync(cancellationToken) is { } max ? max + 1 : 0;

        // Distinct so a selection containing the same title twice cannot violate
        // the unique index within one batch.
        var toAdd = animeIds.Distinct().Where(id => !queued.Contains(id)).ToList();

        // Only titles that actually exist; a stale selection must not create a
        // slot pointing at nothing.
        var existing = await context.Anime
            .Where(a => toAdd.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        var existingIds = existing.ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var added = 0;

        foreach (var animeId in toAdd.Where(existingIds.Contains))
        {
            context.QueueItems.Add(new QueueItem
            {
                ProfileId = profileId,
                Position = nextPosition++,
                AnimeId = animeId,
                AddedAt = now
            });

            added++;
            progress?.Report(new OperationProgress("Adding to Up Next", added, toAdd.Count));
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var skipped = animeIds.Distinct().Count() - added;

        logger.LogInformation(
            "Queue changed: {Added} added, {Skipped} skipped as already queued or missing",
            added,
            skipped);

        return new QueueAddResult(added, skipped);
    }

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
}

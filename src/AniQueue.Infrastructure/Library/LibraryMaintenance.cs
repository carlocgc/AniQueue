using AniQueue.Core.Library;
using AniQueue.Infrastructure.Artwork;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Library;

public sealed class LibraryMaintenance(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    CoverArtStore store,
    ILogger<LibraryMaintenance> logger) : ILibraryMaintenance
{
    public async Task<LibraryContents> GetContentsAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var titles = await context.LibraryEntries
            .CountAsync(e => e.ProfileId == profileId, cancellationToken);

        var queued = await context.QueueItems
            .CountAsync(q => q.ProfileId == profileId, cancellationToken);

        return new LibraryContents(titles, queued, store.CountCached());
    }

    public Task<int> DeleteArtworkAsync(CancellationToken cancellationToken = default)
    {
        // An empty claim set makes the existing sweep delete the whole tree, which is
        // what "no row claims any of this" already means to it.
        var removed = store.RemoveUnclaimed(new HashSet<string>());

        logger.LogInformation("Deleted {Removed} cached picture files", removed);

        return Task.FromResult(removed);
    }

    public async Task<LibraryContents> DeleteEverythingAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        var before = await GetContentsAsync(profileId, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Children before parents, explicitly, rather than leaning on the cascades.
        // These are set-based deletes that never load a row, so nothing here consults
        // the model's delete behaviour — the order is the only thing keeping the
        // foreign keys satisfied, which makes it worth being able to read.
        await context.RecommendationRunItems.ExecuteDeleteAsync(cancellationToken);
        await context.RecommendationRuns.ExecuteDeleteAsync(cancellationToken);

        await context.QueueItems.ExecuteDeleteAsync(cancellationToken);
        await context.LibraryEntries.ExecuteDeleteAsync(cancellationToken);

        await context.AnimeGenres.ExecuteDeleteAsync(cancellationToken);
        await context.AnimeStudios.ExecuteDeleteAsync(cancellationToken);
        await context.AnimeImages.ExecuteDeleteAsync(cancellationToken);
        await context.AnimeRelations.ExecuteDeleteAsync(cancellationToken);
        await context.AnimeExternalIds.ExecuteDeleteAsync(cancellationToken);
        await context.Anime.ExecuteDeleteAsync(cancellationToken);

        // The two vocabularies go with the titles they described. Left behind they
        // would be a filter offering genres nothing in the library has.
        await context.Genres.ExecuteDeleteAsync(cancellationToken);
        await context.Studios.ExecuteDeleteAsync(cancellationToken);

        await context.SyncRuns.ExecuteDeleteAsync(cancellationToken);
        await context.JobRuns.ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // After the rows, so a failure above leaves the pictures where the rows still
        // expect them. Every file is unclaimed now, which is what empties the tree.
        var pictures = store.RemoveUnclaimed(new HashSet<string>());

        logger.LogWarning(
            "Library deleted: {Titles} titles, {Queued} queued, {Pictures} pictures",
            before.Titles,
            before.Queued,
            pictures);

        return before with { Pictures = pictures };
    }
}

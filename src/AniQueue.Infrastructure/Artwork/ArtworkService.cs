using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Artwork;

/// <summary>
/// Fetches the covers nothing has fetched yet, paced, with nobody watching (D47).
/// </summary>
/// <remarks>
/// <b>Outstanding work is a disagreement, not a flag.</b> A row's <c>RemoteUrl</c> is
/// the picture that should be shown and its <c>FetchedUrl</c> is the picture being
/// shown, so "these differ" covers a title whose art has never been fetched and one
/// whose art AniList has replaced, with no timestamp and no schedule involved. Add
/// the one thing a table cannot know — whether the file is still on disk — and that
/// is the whole precondition.
///
/// <b>Everything that can be decided without a socket is.</b> The host check, the
/// content type and the size cap are <see cref="ImageSource"/>'s, which is pure and
/// tested exhaustively; what is left here is the order to work in, when to stop, and
/// which failures are worth trying again.
/// </remarks>
public sealed class ArtworkService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    CoverArtStore store,
    ICoverArtClient client,
    ILogger<ArtworkService> logger,
    TimeProvider? timeProvider = null) : IArtworkService
{
    /// <summary>
    /// Transient failures allowed per URL before the row is left alone.
    /// </summary>
    /// <remarks>
    /// The bound lives on the row because D40 took rescheduling away from jobs, and
    /// something still has to stop an unreachable title being asked about on every
    /// tick forever. Five is enough to ride out a restart of whatever was in the way
    /// and few enough that a genuinely dead URL stops costing requests within a day.
    /// It resets when the URL changes, which is the only event that makes the
    /// question worth asking again.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>
    /// A quarter of a second between requests.
    /// </summary>
    /// <remarks>
    /// Nothing here is urgent, and the alternative — issuing eight hundred requests
    /// as fast as the socket allows — is how an application looks like something to
    /// be blocked. At this spacing a measured 810-title library took four minutes and
    /// six seconds, which is invisible to somebody who is not watching, and each
    /// picture appears as it lands rather than all of them at the end.
    ///
    /// A cover is a static asset on a CDN rather than a call against the GraphQL rate
    /// limit, so <c>RelationPacing</c>'s two seconds would be borrowing a constraint
    /// that does not apply here.
    /// </remarks>
    private static readonly TimeSpan BetweenRequests = TimeSpan.FromMilliseconds(250);

    /// <summary>How many results are written per transaction.</summary>
    /// <remarks>
    /// Small, so that a pass stopped by its budget or by <i>Cancel</i> keeps almost
    /// everything it fetched, and large enough that a first run is not eight hundred
    /// separate writes.
    /// </remarks>
    private const int BatchSize = 25;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ArtworkPassResult> RunAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        if (!store.IsAvailable)
        {
            // No data directory to cache into. True for an in-memory database, and
            // not a failure — there is simply nowhere for this to happen.
            return new ArtworkPassResult();
        }

        var healed = await HealVanishedFilesAsync(cancellationToken);
        var pending = await LoadPendingAsync(cancellationToken);

        var fetched = 0;
        var failed = 0;
        var considered = 0;
        var startedAt = _time.GetTimestamp();

        foreach (var batch in pending.Chunk(BatchSize))
        {
            var outcomes = new List<FetchOutcome>(batch.Length);

            foreach (var image in batch)
            {
                if (cancellationToken.IsCancellationRequested || _time.GetElapsedTime(startedAt) >= budget)
                {
                    break;
                }

                if (considered > 0)
                {
                    await Task.Delay(BetweenRequests, _time, cancellationToken);
                }

                considered++;
                var outcome = await FetchAsync(image, cancellationToken);
                outcomes.Add(outcome);

                if (outcome.Succeeded)
                {
                    fetched++;
                }
                else
                {
                    failed++;
                }
            }

            if (outcomes.Count > 0)
            {
                await RecordAsync(outcomes, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested || _time.GetElapsedTime(startedAt) >= budget)
            {
                break;
            }
        }

        var removed = await RemoveOrphansAsync(cancellationToken);

        if (failed > 0)
        {
            // Logged as information rather than a warning, and never surfaced. Every
            // use of enrichment is an enhancement, so a cover that did not arrive
            // means one row is missing a detail — D25 is explicit that this is
            // deliberately unlike a stalled sync, which means the library is wrong.
            logger.LogInformation(
                "Cover art pass fetched {Fetched} and could not fetch {Failed}", fetched, failed);
        }

        return new ArtworkPassResult
        {
            Considered = considered,
            Fetched = fetched,
            Failed = failed,
            Removed = removed,
            Healed = healed
        };
    }

    /// <summary>
    /// Sends rows whose cached file has gone back to the pending set.
    /// </summary>
    /// <remarks>
    /// This is "disk wins" in one method. The alternative — trusting the table — has
    /// no way back from somebody deleting the covers directory to reclaim space,
    /// which is a reasonable thing to do to a cache and which the repository's own
    /// guidance encourages for the sample profile's data. Statting one file per title
    /// once a tick is microseconds, and it is the difference between a cache and a
    /// cache with a manual repair procedure nobody wrote down.
    /// </remarks>
    private async Task<int> HealVanishedFilesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var claimed = await context.AnimeImages
            .Where(i => i.ContentHash != null && i.FetchedUrl == i.RemoteUrl)
            .Select(i => new { i.Id, i.AnimeId, i.ContentHash, i.FileExtension })
            .ToListAsync(cancellationToken);

        var missing = claimed
            .Where(i => !store.Exists(i.AnimeId, i.ContentHash, i.FileExtension))
            .Select(i => i.Id)
            .ToList();

        if (missing.Count == 0)
        {
            return 0;
        }

        logger.LogInformation("{Count} cached covers are no longer on disk; refetching", missing.Count);

        return await context.AnimeImages
            .Where(i => missing.Contains(i.Id))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(i => i.ContentHash, (string?)null)
                    .SetProperty(i => i.FetchedUrl, (string?)null)
                    .SetProperty(i => i.FileExtension, (string?)null)
                    .SetProperty(i => i.ByteCount, (long?)null)
                    .SetProperty(i => i.FetchedAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    /// <summary>
    /// What is outstanding, in the order somebody is most likely to look at it.
    /// </summary>
    /// <remarks>
    /// Queued first, then planning, then everything else. <b>This is precondition
    /// ordering, not orchestration</b> (D25, D28): remove it and the pass still
    /// converges on exactly the same set, one arbitrary order later. What it buys is
    /// that the first thing a user sees fill in is Up Next, which is the page the
    /// decision is actually made on.
    /// </remarks>
    private async Task<List<PendingImage>> LoadPendingAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Projected to an anonymous type and mapped after materialising, not straight
        // into PendingImage. EF cannot see through a record constructor, so ordering
        // by a property of one fails to translate — at run time, with a clean build
        // and a green suite behind it, which is the same shape of trap §8 records for
        // DateTimeOffset comparison.
        var rows = await context.AnimeImages
            .Where(i => !i.FailureIsPermanent && i.AttemptCount < MaxAttempts)
            .Where(i => i.ContentHash == null || i.FetchedUrl != i.RemoteUrl)
            .Select(i => new
            {
                i.Id,
                i.AnimeId,
                i.RemoteUrl,
                Priority = context.QueueItems.Any(q => q.AnimeId == i.AnimeId)
                    ? 0
                    : context.LibraryEntries.Any(e => e.AnimeId == i.AnimeId && e.Status == LibraryStatus.Planning)
                        ? 1
                        : 2
            })
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(r => new PendingImage(r.Id, r.AnimeId, r.RemoteUrl, r.Priority));
    }

    /// <summary>
    /// Fetches one picture and puts it on disk, or records why it is not there.
    /// </summary>
    /// <remarks>
    /// The guards are the client's; what is decided here is only that a picture which
    /// arrived gets hashed and written before anything claims it is cached. The order
    /// matters: a row saying "cached" whose file is not yet on disk is a broken image,
    /// and the write is atomic so the file exists whole or not at all.
    /// </remarks>
    private async Task<FetchOutcome> FetchAsync(PendingImage image, CancellationToken cancellationToken)
    {
        var fetch = await client.FetchAsync(image.RemoteUrl, cancellationToken);

        if (fetch.Status is not CoverArtFetchStatus.Fetched)
        {
            return fetch.Status is CoverArtFetchStatus.PermanentlyUnavailable
                ? FetchOutcome.Permanent(image)
                : FetchOutcome.Transient(image);
        }

        var content = fetch.Content!;

        // Of the bytes, not of the URL. The bytes are what is being served, and the
        // hash is what makes the address they are served from immutable — so it has
        // to describe the thing behind the address rather than the address itself.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

        await store.WriteAsync(image.AnimeId, hash, fetch.FileExtension!, content, cancellationToken);

        return FetchOutcome.Success(image, hash, fetch.FileExtension!, content.Length);
    }

    private async Task RecordAsync(List<FetchOutcome> outcomes, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ids = outcomes.Select(o => o.Image.Id).ToList();
        var rows = await context.AnimeImages
            .Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        var now = _time.GetUtcNow();

        foreach (var outcome in outcomes)
        {
            if (!rows.TryGetValue(outcome.Image.Id, out var row))
            {
                // The title left the library while its cover was in flight. The file
                // it wrote is already an orphan and this pass's own sweep takes it.
                continue;
            }

            if (outcome.Succeeded)
            {
                row.ContentHash = outcome.ContentHash;
                row.FileExtension = outcome.FileExtension;
                row.ByteCount = outcome.ByteCount;

                // Recorded together, and this is the pair that matters: the row now
                // claims to be showing the picture at this exact address, which is
                // what stops it being picked up again next tick.
                row.FetchedUrl = outcome.Image.RemoteUrl;
                row.FetchedAt = now;

                row.FailedAt = null;
                row.FailureIsPermanent = false;
                row.AttemptCount = 0;

                continue;
            }

            row.FailedAt = now;
            row.FailureIsPermanent = outcome.IsPermanent;

            if (!outcome.IsPermanent)
            {
                row.AttemptCount++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> RemoveOrphansAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var claimed = await context.AnimeImages
            .Where(i => i.ContentHash != null && i.FileExtension != null)
            .Select(i => new { i.AnimeId, i.ContentHash, i.FileExtension })
            .ToListAsync(cancellationToken);

        var names = claimed
            .Select(i => CoverImageResolver.CacheFileName(i.AnimeId, i.ContentHash!, i.FileExtension!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return store.RemoveUnclaimed(names);
    }

    private sealed record PendingImage(int Id, int AnimeId, string RemoteUrl, int Priority);

    private sealed record FetchOutcome(
        PendingImage Image,
        bool Succeeded,
        bool IsPermanent,
        string? ContentHash = null,
        string? FileExtension = null,
        long ByteCount = 0)
    {
        public static FetchOutcome Success(PendingImage image, string hash, string extension, long bytes) =>
            new(image, Succeeded: true, IsPermanent: false, hash, extension, bytes);

        public static FetchOutcome Permanent(PendingImage image) =>
            new(image, Succeeded: false, IsPermanent: true);

        public static FetchOutcome Transient(PendingImage image) =>
            new(image, Succeeded: false, IsPermanent: false);
    }
}

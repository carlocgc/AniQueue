using AniQueue.Core.Domain;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Fetches relations for titles that have never been asked about, a batch at a
/// time, paced against the rate limit.
/// </summary>
/// <remarks>
/// The whole design is one property: <b>asking is recorded, not answering</b>.
/// Roughly half a library is standalone and will never have an edge, so a marker
/// meaning "we got relations" would put every one of those titles back in the queue
/// on every pass — a backfill that never finishes, spending a rate limit on
/// questions already answered.
///
/// Everything else follows from being allowed to fail. A batch that comes back
/// unreadable marks nothing and stops the visit, so the next one asks again; a batch
/// that comes back fine marks every title it asked about, including the ones the
/// source declined to mention.
/// </remarks>
public sealed class RelationBackfillService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IAniListClient client,
    IOptionsMonitor<SyncOptions> options,
    ILogger<RelationBackfillService> logger,
    TimeProvider? timeProvider = null) : IRelationBackfill
{
    /// <summary>
    /// The service whose identifiers the graph is written in.
    /// </summary>
    /// <remarks>
    /// AniList and only AniList, because it is the only source AniQueue reads that
    /// publishes relations at all — a MyAnimeList export carries none (D10). A
    /// library that has never synced AniList therefore gets nothing from this,
    /// which D23 records as a real gap rather than an oversight.
    /// </remarks>
    private const AnimeSource Source = AnimeSource.AniList;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<RelationCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = context.AnimeExternalIds.AsNoTracking().Where(x => x.Source == Source);

        var total = await rows.CountAsync(cancellationToken);

        if (total == 0)
        {
            return RelationCoverage.None;
        }

        var known = await rows.CountAsync(x => x.RelationsFetchedAt != null, cancellationToken);

        return new RelationCoverage(known, total);
    }

    public async Task<RelationBackfillResult> RunAsync(
        int maxRequests,
        CancellationToken cancellationToken = default)
    {
        if (maxRequests <= 0)
        {
            return RelationBackfillResult.Idle;
        }

        // The kill switch stops this as surely as it stops a sync (D25). Unattended
        // outbound traffic is exactly what it exists to halt, and an operator who has
        // turned sync off because it is hammering something would not expect a second
        // thing to carry on talking to the same host.
        if (!options.CurrentValue.Enabled)
        {
            return RelationBackfillResult.Idle;
        }

        var requested = 0;
        var answered = 0;
        var edges = 0;

        // What the last response said about the budget. Local to the visit: a
        // remaining count is a fact about a window that has long since rolled over
        // by the time this service is resolved again.
        int? remaining = null;
        TimeSpan? retryAfter = null;

        for (var request = 0; request < maxRequests; request++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await NextBatchAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            // Paced before the request rather than after it, so the wait belongs to
            // the batch that is about to be sent. Skipped for the first, because a
            // visit that asks about fifty titles and then stops should not have spent
            // two seconds doing nothing first.
            if (request > 0)
            {
                await Task.Delay(RelationPacing.DelayBefore(remaining, retryAfter), _time, cancellationToken);
            }

            var fetch = await client.FetchRelationsAsync(batch, cancellationToken);

            remaining = fetch.RateLimitRemaining;
            retryAfter = fetch.RetryAfter;

            if (!fetch.Succeeded || fetch.Payload is null)
            {
                // Nothing is marked, so the same titles are asked about next time.
                // The visit ends rather than trying the next batch: whatever refused
                // this one will refuse that one too, and the runner's own backoff is
                // the right place to wait it out.
                logger.LogWarning(
                    "Relation backfill stopped after {Requested} titles: {Reason}",
                    requested,
                    fetch.FailureReason);

                return new RelationBackfillResult(requested, answered, edges, fetch.FailureReason);
            }

            var parsed = AniListRelationsParser.Parse(fetch.Payload);

            if (!parsed.Succeeded)
            {
                logger.LogWarning(
                    "Relation response could not be read after {Requested} titles: {Reason}",
                    requested,
                    parsed.FailureReason);

                return new RelationBackfillResult(requested, answered, edges, parsed.FailureReason);
            }

            requested += batch.Count;
            answered += parsed.Titles.Count;
            edges += await ApplyAsync(batch, parsed.Titles, cancellationToken);
        }

        if (requested > 0)
        {
            logger.LogInformation(
                "Relation backfill asked about {Requested} titles and stored {Edges} edge(s)",
                requested,
                edges);
        }

        return new RelationBackfillResult(requested, answered, edges);
    }

    /// <summary>
    /// The next titles nobody has asked about.
    /// </summary>
    /// <remarks>
    /// Ordered by key so a run that is interrupted resumes where it stopped rather
    /// than re-drawing an arbitrary slice, and unfiltered by library status: a
    /// completed prequel has to be displayable as somebody else's relative, so its
    /// edges are as necessary as a planned title's.
    /// </remarks>
    private async Task<List<string>> NextBatchAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AnimeExternalIds
            .AsNoTracking()
            .Where(x => x.Source == Source && x.RelationsFetchedAt == null)
            .OrderBy(x => x.Id)
            .Select(x => x.ExternalId)
            .Take(AniListClient.MaxRelationIdsPerRequest)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores what came back, and marks every title that was asked about.
    /// </summary>
    /// <remarks>
    /// One transaction per batch. A visit that fails halfway leaves the batches
    /// before it committed and the rest unmarked, which is the shape this whole
    /// design wants: partial progress is progress, because the marker makes
    /// resumption free.
    /// </remarks>
    private async Task<int> ApplyAsync(
        IReadOnlyCollection<string> asked,
        IReadOnlyList<ParsedRelations> titles,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var now = _time.GetUtcNow();
        var written = 0;

        // Read once for the batch rather than per edge. Fifty titles produce a few
        // hundred edges, and a contains-query over one batch's worth of identifiers
        // beats several hundred round trips to discover that nothing has changed.
        var existing = await context.AnimeRelations
            .Where(r => r.Source == Source && asked.Contains(r.ExternalId))
            .Select(r => new { r.ExternalId, r.RelationType, r.RelatedExternalId })
            .ToListAsync(cancellationToken);

        var known = existing
            .Select(r => (r.ExternalId, r.RelationType, r.RelatedExternalId))
            .ToHashSet();

        foreach (var title in titles)
        {
            foreach (var relation in title.Relations)
            {
                if (!known.Add((title.ExternalId, relation.Type, relation.RelatedExternalId)))
                {
                    continue;
                }

                context.AnimeRelations.Add(new AnimeRelation
                {
                    Source = Source,
                    ExternalId = title.ExternalId,
                    RelationType = relation.Type,
                    RelatedExternalId = relation.RelatedExternalId
                });

                written++;
            }
        }

        // Catalogue fields ride along on the same request, and follow the import
        // path's rule exactly: a value replaces what is stored, a null leaves it
        // alone. Enrichment may only add (D18, D25) — nothing here touches status,
        // progress or score, and none of those are even loaded.
        var byExternalId = titles.ToDictionary(t => t.ExternalId);

        var rows = await context.AnimeExternalIds
            .Include(x => x.Anime)
            .Where(x => x.Source == Source && asked.Contains(x.ExternalId))
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            // Marked whether or not the source said anything about it. This is the
            // "we asked" property, and it is what stops a standalone title being
            // asked about forever.
            row.RelationsFetchedAt = now;

            if (row.Anime is null || !byExternalId.TryGetValue(row.ExternalId, out var title))
            {
                continue;
            }

            if (title.StartDate is { } startDate)
            {
                row.Anime.StartDate = startDate;
            }

            // ReleaseYear is deliberately left alone, even though a start date
            // obviously implies one. It is written from AniList's seasonYear, which
            // is not the same number: a series first airing in December 2015 belongs
            // to the Winter 2016 season, and both are correct about different things.
            // Writing one from the other here would fight the next sync for the
            // column, and the decade filter would flip between runs.

            if (title.CoverImageColor is { } colour)
            {
                row.Anime.CoverImageColor = colour;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return written;
    }
}

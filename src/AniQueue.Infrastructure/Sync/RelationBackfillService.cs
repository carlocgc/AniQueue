using AniQueue.Core.Domain;
using AniQueue.Core.Progress;
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
    /// publishes relations at all — a MyAnimeList export carries none. A
    /// library that has never synced AniList therefore gets nothing from this,
    /// which is a known gap rather than an oversight.
    /// </remarks>
    private const AnimeSource Source = AnimeSource.AniList;

    /// <summary>
    /// How long an answer is trusted before the title is asked about again.
    /// </summary>
    /// <remarks>
    /// Relations are near-static but not static: editors reclassify a side story as
    /// a spin-off, add a recap film's link, or correct an edge that was wrong. The
    /// case a refresh exists for is <b>both ends already owned</b> — a brand new
    /// sequel needs no refresh at all, because it arrives as a new title with no
    /// marker and its own edges point back at what it follows.
    ///
    /// Thirty days rather than seven, and fixed rather than configurable. The graph
    /// changes on the timescale of production announcements, and "how often should
    /// relation metadata be re-read" is a question nobody has an opinion about — a
    /// setting for it would be a control that is never touched and a migration to
    /// carry it. The button on the Sources page covers impatience.
    ///
    /// Deliberately <i>not</i> narrowed to titles still airing, which would cut the
    /// population by most of it: the interesting case is a finished show from 2005
    /// gaining a sequel announced in 2026, and status-based targeting misses exactly
    /// that.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

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

    public async Task<int> ForgetAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var deleted = await context.AnimeRelations
            .Where(r => r.Source == Source)
            .ExecuteDeleteAsync(cancellationToken);

        // In the same transaction as the delete, because half of this is a graph with
        // no edges and the other half is a library that thinks it has already asked.
        // Either alone is worse than neither: the first rebuilds on the next run, the
        // second stays empty for thirty days.
        await context.AnimeExternalIds
            .Where(x => x.Source == Source)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RelationsFetchedAt, (DateTime?)null),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Deleted {Edges} relation edges and forgot every marker", deleted);

        return deleted;
    }

    public async Task<RelationBackfillResult> RunAsync(
        TimeSpan budget,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (budget <= TimeSpan.Zero)
        {
            return RelationBackfillResult.Idle;
        }

        // The kill switch stops this as surely as it stops a sync. Unattended
        // outbound traffic is exactly what it exists to halt, and an operator who has
        // turned sync off because it is hammering something would not expect a second
        // thing to carry on talking to the same host.
        if (!options.CurrentValue.Enabled)
        {
            return RelationBackfillResult.Idle;
        }

        var outstanding = await OutstandingAsync(cancellationToken);

        if (outstanding == 0)
        {
            return RelationBackfillResult.Idle;
        }

        const string Message = "Reading related titles";
        progress?.Report(new OperationProgress(Message, 0, outstanding));

        var requested = 0;
        var answered = 0;
        var edges = 0;
        var removed = 0;

        // What the last response said about the budget. Local to the visit: a
        // remaining count is a fact about a window that has long since rolled over
        // by the time this service is resolved again.
        int? remaining = null;
        TimeSpan? retryAfter = null;

        // Bounded by time rather than by count, so the pass finishes the work instead
        // of a fixed slice of it. The budget is checked between requests,
        // never inside one: abandoning a request in flight would waste the two seconds
        // already spent waiting for its slot.
        var startedAt = _time.GetTimestamp();
        var ranOutOfTime = false;

        for (var request = 0; ; request++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request > 0 && _time.GetElapsedTime(startedAt) >= budget)
            {
                ranOutOfTime = true;
                break;
            }

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

                return new RelationBackfillResult(requested, answered, edges, removed, fetch.FailureReason);
            }

            var parsed = AniListRelationsParser.Parse(fetch.Payload);

            if (!parsed.Succeeded)
            {
                logger.LogWarning(
                    "Relation response could not be read after {Requested} titles: {Reason}",
                    requested,
                    parsed.FailureReason);

                return new RelationBackfillResult(requested, answered, edges, removed, parsed.FailureReason);
            }

            var applied = await ApplyAsync(batch, parsed.Titles, cancellationToken);

            requested += batch.Count;
            answered += parsed.Titles.Count;
            edges += applied.Written;
            removed += applied.Removed;

            progress?.Report(new OperationProgress(Message, requested, outstanding));
        }

        if (requested > 0)
        {
            logger.LogInformation(
                "Relation backfill asked about {Requested} titles, stored {Edges} and removed {Removed} edge(s)",
                requested,
                edges,
                removed);
        }

        return new RelationBackfillResult(requested, answered, edges, removed, RanOutOfTime: ranOutOfTime);
    }

    /// <summary>
    /// How many titles are waiting to be asked about, so progress has a denominator.
    /// </summary>
    /// <remarks>
    /// Counted once at the start rather than recomputed per batch. It goes out of
    /// date as the pass commits — which is the point, since a bar that recounted its
    /// own remaining work would never move.
    /// </remarks>
    private async Task<int> OutstandingAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var staleBefore = (_time.GetUtcNow() - StaleAfter).UtcDateTime;

        return await context.AnimeExternalIds
            .AsNoTracking()
            .CountAsync(
                x => x.Source == Source
                    && (x.RelationsFetchedAt == null || x.RelationsFetchedAt < staleBefore),
                cancellationToken);
    }

    /// <summary>
    /// The next titles to ask about: never asked first, then anything whose answer
    /// has gone stale.
    /// </summary>
    /// <remarks>
    /// Unfiltered by library status, because a completed prequel has to be
    /// displayable as somebody else's relative — its edges are as necessary as a
    /// planned title's.
    ///
    /// <b>Never ordered by the marker itself.</b> SQLite cannot <c>ORDER BY</c> a
    /// <c>DateTimeOffset</c>: EF stores it as text with an offset and throws at query
    /// time rather than returning a wrong order. Sorting on <i>whether</i> it is
    /// null is a boolean and translates fine, which is all the ordering this needs —
    /// within either group the key is an arbitrary but stable tiebreak, and a stale
    /// title that gets refreshed stops being eligible, so nothing starves.
    /// </remarks>
    private async Task<List<string>> NextBatchAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var staleBefore = (_time.GetUtcNow() - StaleAfter).UtcDateTime;

        return await context.AnimeExternalIds
            .AsNoTracking()
            .Where(x => x.Source == Source
                && (x.RelationsFetchedAt == null || x.RelationsFetchedAt < staleBefore))
            .OrderBy(x => x.RelationsFetchedAt != null)
            .ThenBy(x => x.Id)
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
    private async Task<(int Written, int Removed)> ApplyAsync(
        IReadOnlyCollection<string> asked,
        IReadOnlyList<ParsedRelations> titles,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        var written = 0;

        // Read once for the batch rather than per edge. Fifty titles produce a few
        // hundred edges, and a contains-query over one batch's worth of identifiers
        // beats several hundred round trips to discover that nothing has changed.
        var existing = await context.AnimeRelations
            .Where(r => r.Source == Source && asked.Contains(r.ExternalId))
            .ToListAsync(cancellationToken);

        var known = existing
            .Select(r => (r.ExternalId, r.RelationType, r.RelatedExternalId))
            .ToHashSet();

        // What the source published this time, for the titles it published anything
        // about. This is what makes a refresh worth doing: without it the pass could
        // only ever add, so an edge AniList corrected or withdrew would be confirmed
        // rather than removed, and re-asking would achieve less than half its purpose.
        var stated = titles
            .SelectMany(t => t.Relations.Select(r => (t.ExternalId, r.Type, r.RelatedExternalId)))
            .ToHashSet();

        var answered = titles.Select(t => t.ExternalId).ToHashSet();

        // Absence is scoped: the source's silence is authoritative
        // only where it spoke. A title this response did not mention keeps every edge
        // it had — the batch may simply not have covered it, and deleting on that
        // basis would be reading a gap as a statement.
        var withdrawn = existing
            .Where(r => answered.Contains(r.ExternalId)
                && !stated.Contains((r.ExternalId, r.RelationType, r.RelatedExternalId)))
            .ToList();

        if (withdrawn.Count > 0)
        {
            logger.LogInformation(
                "{Count} relation(s) are no longer published and were removed",
                withdrawn.Count);

            context.AnimeRelations.RemoveRange(withdrawn);
        }

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
        // alone. Enrichment may only add — nothing here touches status,
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

        return (written, withdrawn.Count);
    }
}

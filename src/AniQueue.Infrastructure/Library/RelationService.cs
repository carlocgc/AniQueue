using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Library;

/// <summary>
/// Resolves the relation graph into owned titles, for the row the user expanded.
/// </summary>
/// <remarks>
/// Every read here has the same shape and it is worth stating once: an edge is
/// stored as a pair of external identifiers with no foreign key to anything (D24),
/// so <i>both</i> ends are resolved through <see cref="AnimeExternalId"/> at read
/// time, and both directions are searched. Half of any title's relations are rows
/// where it is the <c>RelatedExternalId</c> rather than the <c>ExternalId</c> —
/// a title whose own relations have never been fetched is reachable only that way
/// — and an edge read from the far end is inverted before it is labelled.
/// </remarks>
public sealed class RelationService(IDbContextFactory<AniQueueDbContext> contextFactory) : IRelationService
{
    /// <summary>
    /// The service whose identifiers the graph is written in.
    /// </summary>
    /// <remarks>
    /// AniList and only AniList, matching the backfill that fills it: no other
    /// source AniQueue reads publishes relations at all (D10). A MyAnimeList-only
    /// library therefore expands nothing, which D23 records as a real gap rather
    /// than an oversight.
    /// </remarks>
    private const AnimeSource Source = AnimeSource.AniList;

    public async Task<IReadOnlyDictionary<int, int>> GetRelatedCountsAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(animeIds);

        if (animeIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Distinct pairs rather than distinct edges. Two titles are frequently
        // joined by more than one row — AniList states the same fact from both
        // ends, and a pair can carry more than one type — and counting rows would
        // put a "3" on a chevron that opens to one relative.
        var pairs = await Edges(context, profileId, animeIds)
            .Select(e => new { e.OwnerAnimeId, e.RelatedAnimeId })
            .Distinct()
            .ToListAsync(cancellationToken);

        // Grouped here rather than in SQL, and only here. The set is bounded by
        // what one page of fifty rows is related to, so it is small by
        // construction — unlike the library itself, which §6 requires be filtered
        // in the database. Pushing a GROUP BY through a UNION of two joins buys
        // nothing on a few hundred rows and is the kind of query that stops
        // translating when something upstream changes shape.
        return pairs
            .GroupBy(p => p.OwnerAnimeId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<IReadOnlyList<RelatedTitle>> GetRelatedAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var edges = await Edges(context, profileId, [animeId])
            .Select(e => new { e.RelatedAnimeId, e.RelationType, e.Inverted })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (edges.Count == 0)
        {
            return [];
        }

        // Inverted in memory because it has to be: the mapping is a switch over an
        // enum (D24), and translating it would mean writing the same table twice in
        // two languages. The set is one title's relatives, so there is nothing to
        // gain by trying.
        var relationByAnime = edges
            .GroupBy(e => e.RelatedAnimeId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var types = g
                        .Select(e => e.Inverted ? RelationTypes.Invert(e.RelationType) : e.RelationType)
                        .Distinct()
                        .Take(2)
                        .ToList();

                    // One agreed type is a label; anything else is "Related". The
                    // disagreement is routine rather than exotic — AniList uses
                    // PARENT as the counterpart of both SIDE_STORY and SPIN_OFF —
                    // and naming one of them would state something the source did
                    // not.
                    return types.Count == 1 ? types[0] : (RelationType?)null;
                });

        var relatedIds = relationByAnime.Keys.ToList();

        var titles = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && relatedIds.Contains(e.AnimeId))
            .OrderBy(e => e.Anime!.StartDate == null)
            .ThenBy(e => e.Anime!.StartDate)

            // The year is a tiebreak rather than the key. A start date is written by
            // the relation pass and a year by the list sync, so a relative nothing
            // has fetched yet has one and not the other — and leaving the dateless
            // group alphabetical would read as a mistake beside a list that is
            // otherwise chronological.
            .ThenBy(e => e.Anime!.ReleaseYear == null)
            .ThenBy(e => e.Anime!.ReleaseYear)
            .ThenBy(e => e.Anime!.Title)
            .Select(e => new
            {
                e.AnimeId,
                e.Anime!.Title,
                e.Anime.MediaType,
                e.Anime.EpisodeCount,
                e.Anime.EpisodeDurationMinutes,
                e.Anime.ReleaseYear,
                e.Anime.StartDate,
                e.Status,
                e.EpisodesWatched
            })
            .ToListAsync(cancellationToken);

        var queued = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId && relatedIds.Contains(q.AnimeId))
            .Select(q => q.AnimeId)
            .ToListAsync(cancellationToken);

        var queuedIds = queued.ToHashSet();

        return
        [
            .. titles.Select(t => new RelatedTitle
            {
                AnimeId = t.AnimeId,
                Title = t.Title,
                MediaType = t.MediaType,
                EpisodeCount = t.EpisodeCount,
                EpisodeDurationMinutes = t.EpisodeDurationMinutes,
                ReleaseYear = t.ReleaseYear,
                StartDate = t.StartDate,
                Status = t.Status,
                EpisodesWatched = t.EpisodesWatched,
                IsQueued = queuedIds.Contains(t.AnimeId),
                Relation = relationByAnime[t.AnimeId]
            })
        ];
    }

    /// <summary>
    /// Every edge one step out from the given titles, in both directions, narrowed
    /// to relatives the profile owns and has not hidden.
    /// </summary>
    /// <remarks>
    /// One step, and the count and the detail share this method so neither can
    /// drift into promising what the other does not show.
    ///
    /// Hidden is the only status excluded. An expansion is context rather than
    /// results — a completed prequel is frequently the most useful thing it can say
    /// — so filtering it the way the listing above it is filtered would empty it of
    /// exactly what it exists for. Hiding is different in kind: it is the user
    /// saying they do not want to see that title anywhere.
    /// </remarks>
    private static IQueryable<Edge> Edges(
        AniQueueDbContext context,
        int profileId,
        IReadOnlyCollection<int> animeIds)
    {
        var identifiers = context.AnimeExternalIds.AsNoTracking().Where(x => x.Source == Source);
        var relations = context.AnimeRelations.AsNoTracking().Where(r => r.Source == Source);

        var owners = identifiers.Where(x => animeIds.Contains(x.AnimeId));

        var owned = context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && !e.IsHidden);

        // Stated by the title itself.
        var forward =
            from owner in owners
            join relation in relations on owner.ExternalId equals relation.ExternalId
            join far in identifiers on relation.RelatedExternalId equals far.ExternalId
            join entry in owned on far.AnimeId equals entry.AnimeId
            where far.AnimeId != owner.AnimeId
            select new Edge
            {
                OwnerAnimeId = owner.AnimeId,
                RelatedAnimeId = far.AnimeId,
                RelationType = relation.RelationType,
                Inverted = false
            };

        // Stated about the title by something else, which is how a title the
        // backfill has not reached yet is found at all.
        var reverse =
            from owner in owners
            join relation in relations on owner.ExternalId equals relation.RelatedExternalId
            join far in identifiers on relation.ExternalId equals far.ExternalId
            join entry in owned on far.AnimeId equals entry.AnimeId
            where far.AnimeId != owner.AnimeId
            select new Edge
            {
                OwnerAnimeId = owner.AnimeId,
                RelatedAnimeId = far.AnimeId,
                RelationType = relation.RelationType,
                Inverted = true
            };

        return forward.Concat(reverse);
    }

    /// <summary>One resolved edge, and which end of it spoke.</summary>
    private sealed record Edge
    {
        public required int OwnerAnimeId { get; init; }

        public required int RelatedAnimeId { get; init; }

        public required RelationType RelationType { get; init; }

        /// <summary>True when the edge was read from the far end and needs inverting.</summary>
        public required bool Inverted { get; init; }
    }
}

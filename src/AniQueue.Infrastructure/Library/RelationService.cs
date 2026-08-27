using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Core.Queue;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Library;

/// <summary>
/// Resolves the relation graph into the set one title belongs to (D55).
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
public sealed class RelationService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IQueueService queue) : IRelationService
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

    /// <summary>
    /// How many times the walk will ask for the next step before giving up.
    /// </summary>
    /// <remarks>
    /// A stop, not a budget. Each step is one indexed query over the frontier, and a
    /// real set is a handful deep — the longest television runs anyone owns are
    /// nowhere near this. It exists because the walk is transitive over data an
    /// external editor maintains, and an unbounded loop over a graph somebody else
    /// can reshape is a page that hangs rather than a page that is wrong. The visited
    /// set already makes cycles terminate; this bounds depth as well.
    /// </remarks>
    private const int MaxWalkSteps = 32;

    /// <summary>
    /// The relation types that mean "the same work", and so make up a set (D55).
    /// </summary>
    /// <remarks>
    /// An array rather than a switch because it has to translate: it is used inside a
    /// query, where EF turns <c>Contains</c> over a local collection into an
    /// <c>IN</c> clause.
    /// </remarks>
    private static readonly RelationType[] SameWork =
    [
        RelationType.Prequel,
        RelationType.Sequel,
        RelationType.Parent,
        RelationType.SideStory
    ];

    public async Task<IReadOnlyList<RelatedTitle>> GetRelatedAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The title itself is in its own set — QueueSetAsync needs it there — and is
        // the one thing this list must not repeat back at the dialog it is open in.
        var others = (await SetAsync(context, profileId, animeId, cancellationToken))
            .Where(e => e.AnimeId != animeId)
            .ToList();

        if (others.Count == 0)
        {
            return [];
        }

        var relationByAnime = await DirectRelationsAsync(context, profileId, animeId, cancellationToken);

        var ids = others.Select(e => e.AnimeId).ToList();

        var queued = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId && ids.Contains(q.AnimeId))
            .Select(q => q.AnimeId)
            .ToListAsync(cancellationToken);

        var queuedIds = queued.ToHashSet();

        return
        [
            .. others.Select(t => new RelatedTitle
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

                // Absent means "further than one edge away", which is most of a long
                // run and is why this is a lookup rather than a required field.
                Relation = relationByAnime.GetValueOrDefault(t.AnimeId)
            })
        ];
    }

    public async Task<int> CountToQueueAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var set = await SetAsync(context, profileId, animeId, cancellationToken);

        if (set.Count == 0)
        {
            return 0;
        }

        // Counted as what would actually be appended, not as the size of the set.
        // The action names its own size — "queue five titles" — and a number that
        // included seasons already queued or already watched would be a promise the
        // press could not keep.
        var queued = await queue.GetQueuedAnimeIdsAsync(profileId, cancellationToken);

        return set.Count(c => c.Status == LibraryStatus.Planning && !queued.Contains(c.AnimeId));
    }

    public async Task<QueueAddResult> QueueSetAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        List<SetEntry> set;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            set = await SetAsync(context, profileId, animeId, cancellationToken);
        }

        if (set.Count == 0)
        {
            return new QueueAddResult { Added = 0 };
        }

        // Handed over whole rather than pre-filtered, so one method decides queue
        // eligibility however a title got here. A Completed season the walk passed
        // through comes back counted as NoLongerPlanned rather than silently dropped,
        // which is the difference between "added 2" and "added 2, skipped the one you
        // have already seen".
        return await queue.AddAnimeAsync(
            profileId,
            [.. set.Select(c => c.AnimeId)],
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The set one title belongs to, owned, in release order, including the title.
    /// </summary>
    /// <remarks>
    /// <b>The four edges a box set is assembled along.</b> Prequel and sequel give
    /// the main seasons; parent and side story give the specials hanging off them and
    /// the main work a special hangs off. Spin-off, alternative, summary, compilation
    /// and contains are not followed at all: a separate work in the same world is a
    /// different purchase, and a remake or a recap is the same story told again
    /// (D55).
    ///
    /// <b>Membership is symmetric, so direction is not consulted.</b> An edge is
    /// stored exactly as fetched (D24), so "this has sequel X" and "X has prequel
    /// this" are the same fact written from opposite ends — and a season whose own
    /// relations have never been fetched is reachable only through the second form.
    /// Either endpoint being in the frontier puts the other in the set.
    ///
    /// The walk happens in <b>external identifiers</b> and resolves to library rows
    /// only at the end, which is what lets it pass through a season the user does not
    /// own: an unowned middle season has edges but no <c>Anime</c> row, so resolving
    /// as it went would end the walk at exactly the gap this exists to bridge.
    /// </remarks>
    private static async Task<List<SetEntry>> SetAsync(
        AniQueueDbContext context,
        int profileId,
        int animeId,
        CancellationToken cancellationToken)
    {
        var start = await context.AnimeExternalIds
            .AsNoTracking()
            .Where(x => x.Source == Source && x.AnimeId == animeId)
            .Select(x => x.ExternalId)
            .ToListAsync(cancellationToken);

        if (start.Count == 0)
        {
            // Nothing AniList identifies has nothing AniList can say belongs with it.
            // The caller offers no action rather than one that would queue only the
            // row the user was already looking at.
            return [];
        }

        var reached = start.ToHashSet(StringComparer.Ordinal);
        var frontier = start;

        for (var step = 0; step < MaxWalkSteps && frontier.Count > 0; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = frontier;

            var next = await context.AnimeRelations
                .AsNoTracking()
                .Where(r => r.Source == Source)
                .Where(r => SameWork.Contains(r.RelationType))
                .Where(r => current.Contains(r.ExternalId) || current.Contains(r.RelatedExternalId))
                .Select(r => current.Contains(r.ExternalId) ? r.RelatedExternalId : r.ExternalId)
                .Distinct()
                .ToListAsync(cancellationToken);

            // The visited set is what makes a cycle terminate. Relation data is
            // maintained by people, and a graph that says two titles follow each other
            // is a mistake this must survive rather than spin on.
            frontier = [.. next.Where(id => reached.Add(id))];
        }

        var excluded = await RecapsAndCompilationsAsync(context, reached, cancellationToken);

        return await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId)
            .Where(e => e.Anime!.ExternalIds.Any(x =>
                x.Source == Source && reached.Contains(x.ExternalId) && !excluded.Contains(x.ExternalId)))

            // Release order, and it is a fact rather than an opinion: AniList
            // publishes no viewing sequence, and ordering along the edges themselves
            // would produce story order, which is frequently the wrong watch order
            // (D24). Unknown dates last, with the year as a tiebreak for anything the
            // relation pass has not reached.
            .OrderBy(e => e.Anime!.StartDate == null)
            .ThenBy(e => e.Anime!.StartDate)
            .ThenBy(e => e.Anime!.ReleaseYear == null)
            .ThenBy(e => e.Anime!.ReleaseYear)
            .ThenBy(e => e.Anime!.Title)
            .Select(e => new SetEntry(
                e.AnimeId,
                e.Anime!.Title,
                e.Anime.MediaType,
                e.Anime.EpisodeCount,
                e.Anime.EpisodeDurationMinutes,
                e.Anime.ReleaseYear,
                e.Anime.StartDate,
                e.Status,
                e.EpisodesWatched))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Which of the reached identifiers are recaps or compilations, and so are not
    /// part of "the rest of this series".
    /// </summary>
    /// <remarks>
    /// Reachable through a <c>SEQUEL</c>-only walk precisely because of how AniList
    /// threads them: a recap film released between two seasons is routinely published
    /// as the sequel of the first and the prequel of the second, so it sits in the
    /// middle of the chain rather than hanging off it. Nobody asking for the rest of a
    /// series means the summary of the part they just watched.
    ///
    /// <b>Direction matters, and one direction cannot be read.</b> An edge saying
    /// "X has compilation Y" names Y, and "Y contains X" names Y again, so both forms
    /// identify the compilation. <c>SUMMARY</c> has no inverse in AniList's vocabulary
    /// — <see cref="RelationTypes.Invert"/> maps it to itself — so only the form
    /// stating "X has summary Y" identifies the recap. A recap whose own fetch stated
    /// the edge from its side is indistinguishable from the series it recaps, and is
    /// therefore left in. Excluding both ends would drop the season instead, which is
    /// much worse than queueing a recap the user can remove in one press.
    /// </remarks>
    private static async Task<HashSet<string>> RecapsAndCompilationsAsync(
        AniQueueDbContext context,
        IReadOnlyCollection<string> reached,
        CancellationToken cancellationToken)
    {
        var named = await context.AnimeRelations
            .AsNoTracking()
            .Where(r => r.Source == Source)
            .Where(r =>
                ((r.RelationType == RelationType.Summary || r.RelationType == RelationType.Compilation)
                    && reached.Contains(r.RelatedExternalId))
                || (r.RelationType == RelationType.Contains && reached.Contains(r.ExternalId)))
            .Select(r => r.RelationType == RelationType.Contains ? r.ExternalId : r.RelatedExternalId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return named.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>One owned title in a set, with everything either caller needs.</summary>
    /// <remarks>
    /// The queue actions want the id and the status; the dialog wants the rest. One
    /// record for both, because a set is a dozen rows at most and reading two shapes
    /// of it would mean walking the graph twice.
    /// </remarks>
    private sealed record SetEntry(
        int AnimeId,
        string Title,
        MediaType MediaType,
        int? EpisodeCount,
        int? EpisodeDurationMinutes,
        int? ReleaseYear,
        DateOnly? StartDate,
        LibraryStatus Status,
        int EpisodesWatched);

    /// <summary>
    /// How the source labels the titles exactly one edge from this one.
    /// </summary>
    /// <remarks>
    /// Only the direct neighbours get a label, and only where the two ends agree.
    /// Anything further into the set has no edge to read, so it has no name for why
    /// it is there — which the dialog renders as no badge rather than as a guess
    /// (D55).
    ///
    /// Inverted in memory because it has to be: the mapping is a switch over an enum
    /// (D24), and translating it would mean writing the same table twice in two
    /// languages. The set is one title's neighbours, so there is nothing to gain by
    /// trying.
    /// </remarks>
    private static async Task<Dictionary<int, RelationType?>> DirectRelationsAsync(
        AniQueueDbContext context,
        int profileId,
        int animeId,
        CancellationToken cancellationToken)
    {
        var edges = await Edges(context, profileId, [animeId])
            .Select(e => new { e.RelatedAnimeId, e.RelationType, e.Inverted })
            .Distinct()
            .ToListAsync(cancellationToken);

        return edges
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
    }

    /// <summary>
    /// Every edge one step out from the given titles, in both directions, narrowed
    /// to relatives the profile owns.
    /// </summary>
    /// <remarks>
    /// This is now only what labels a neighbour, not what decides membership — the
    /// walk in <see cref="SetAsync"/> does that, and goes further (D55). It stays a
    /// query over both directions because an edge is stored exactly as fetched.
    ///
    /// No status is excluded. A set is context rather than results — a completed
    /// prequel is frequently the most useful thing it can say — so filtering it the
    /// way the listing above it is filtered would empty it of exactly what it exists
    /// for. Hidden used to be the one exception, on the grounds that it was the user
    /// saying they did not want to see that title anywhere; Phase 18b deleted hiding.
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
            .Where(e => e.ProfileId == profileId);

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

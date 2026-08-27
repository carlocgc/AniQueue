using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Library;

public sealed class LibraryService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ILogger<LibraryService> logger) : ILibraryService
{
    public async Task<LibrarySummary> GetSummaryAsync(int profileId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Grouped in the database rather than counted per status in a loop, which
        // would be one query per status for the same information.
        var counts = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        return new LibrarySummary { Total = counts.Values.Sum(), ByStatus = counts };
    }

    public async Task<LibraryPage> GetPageAsync(
        int profileId,
        LibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var filtered = ApplyFilters(context, context.LibraryEntries.AsNoTracking(), profileId, query);
        var total = await filtered.CountAsync(cancellationToken);

        // Loaded once for the page rather than joined per row: the queue is small
        // by design, and a set lookup beats a correlated subquery per entry.
        var queued = await context.QueueItems
            .AsNoTracking()
            .Where(q => q.ProfileId == profileId)
            .Select(q => q.AnimeId)
            .ToListAsync(cancellationToken);

        var queuedIds = queued.ToHashSet();

        var items = await ApplySort(filtered, query.Sort)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(e => new
            {
                e.AnimeId,
                e.Anime!.Title,
                e.Anime.MediaType,
                e.Anime.EpisodeCount,
                e.Anime.EpisodeDurationMinutes,
                e.Anime.ReleaseYear,
                e.Status,
                e.EpisodesWatched,
                e.UserScore,
                e.RecommendationScore,
                e.RecommendationConfidence,
                e.Anime.Source,
                e.Anime.CoverImageColor,

                // The cached poster, as a scalar rather than a collection. It is a
                // subquery per row instead of a join because the row wants at most
                // one of them and a join would multiply the page by however many
                // images a title has (D47). The multiplier was to be image kinds;
                // D48 declined the sources those needed, and it is renditions of the
                // one poster instead — which changes the count and not the argument.
                CoverContentHash = e.Anime.Images
                    .Where(x => x.Kind == ImageKind.Poster && x.Rendition == ImageRendition.Thumbnail && x.ContentHash != null)
                    .Select(x => x.ContentHash)
                    .FirstOrDefault(),
                CoverFileExtension = e.Anime.Images
                    .Where(x => x.Kind == ImageKind.Poster && x.Rendition == ImageRendition.Thumbnail && x.ContentHash != null)
                    .Select(x => x.FileExtension)
                    .FirstOrDefault(),

                // Projected to an anonymous shape and mapped after materialising:
                // a collection projection translates to a join, and building the
                // domain record here would depend on constructor translation.
                ExternalIds = e.Anime.ExternalIds
                    .Select(x => new { x.Source, x.ExternalId })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return new LibraryPage
        {
            TotalCount = total,
            Items = items.Select(i => new LibraryListItem
            {
                AnimeId = i.AnimeId,
                Title = i.Title,
                MediaType = i.MediaType,
                EpisodeCount = i.EpisodeCount,
                EpisodeDurationMinutes = i.EpisodeDurationMinutes,
                ReleaseYear = i.ReleaseYear,
                Status = i.Status,
                EpisodesWatched = i.EpisodesWatched,
                UserScore = i.UserScore,
                RecommendationScore = i.RecommendationScore,
                RecommendationConfidence = i.RecommendationConfidence,
                Source = i.Source,
                ExternalIds = [.. i.ExternalIds.Select(x => new ExternalIdentifier(x.Source, x.ExternalId))],
                IsQueued = queuedIds.Contains(i.AnimeId),
                CoverContentHash = i.CoverContentHash,
                CoverFileExtension = i.CoverFileExtension,
                CoverImageColor = i.CoverImageColor
            }).ToList()
        };
    }

    /// <summary>
    /// Loads one title in the detail the dialog argues with (D49).
    /// </summary>
    /// <remarks>
    /// <b>Split rather than joined.</b> Genres, studios, identifiers and the two
    /// poster renditions are four collections hanging off one row, and a single query
    /// multiplies them together — four genres, five studios, two identifiers and two
    /// images is eighty rows to build one object from. EF warns about exactly this by
    /// name, so the query splits, which costs a handful of round trips at human speed
    /// to read one title somebody just clicked.
    ///
    /// The renditions are read as two scalars rather than a collection because the
    /// dialog wants at most one of each, and picking between them is
    /// <see cref="TitleDetail.Poster"/>'s job.
    /// </remarks>
    public async Task<TitleDetail?> GetTitleDetailAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && e.AnimeId == animeId)
            .Select(e => new
            {
                e.AnimeId,
                e.Anime!.Title,
                e.Anime.MediaType,
                e.Anime.EpisodeCount,
                e.Anime.EpisodeDurationMinutes,
                e.Anime.ReleaseYear,
                e.Anime.Description,
                e.Anime.Source,
                e.Anime.CoverImageColor,
                e.Status,
                e.EpisodesWatched,
                e.UserScore,
                e.RecommendationScore,
                e.RecommendationConfidence,
                e.RecommendationReason,

                Genres = e.Anime.Genres
                    .Select(g => g.Genre!.Name)
                    .OrderBy(name => name)
                    .ToList(),

                // The one AniList flags as primary, and null when it flags none —
                // which is common enough that the dialog is built to omit the line
                // rather than to promote whichever company came back first (D49).
                MainStudio = e.Anime.Studios
                    .Where(s => s.IsMain)
                    .Select(s => s.Studio!.Name)
                    .FirstOrDefault(),

                ExternalIds = e.Anime.ExternalIds
                    .Select(x => new { x.Source, x.ExternalId })
                    .ToList(),

                PosterContentHash = e.Anime.Images
                    .Where(i => i.Kind == ImageKind.Poster
                        && i.Rendition == ImageRendition.Full
                        && i.ContentHash != null)
                    .Select(i => i.ContentHash)
                    .FirstOrDefault(),
                PosterFileExtension = e.Anime.Images
                    .Where(i => i.Kind == ImageKind.Poster
                        && i.Rendition == ImageRendition.Full
                        && i.ContentHash != null)
                    .Select(i => i.FileExtension)
                    .FirstOrDefault(),
                ThumbnailContentHash = e.Anime.Images
                    .Where(i => i.Kind == ImageKind.Poster
                        && i.Rendition == ImageRendition.Thumbnail
                        && i.ContentHash != null)
                    .Select(i => i.ContentHash)
                    .FirstOrDefault(),
                ThumbnailFileExtension = e.Anime.Images
                    .Where(i => i.Kind == ImageKind.Poster
                        && i.Rendition == ImageRendition.Thumbnail
                        && i.ContentHash != null)
                    .Select(i => i.FileExtension)
                    .FirstOrDefault(),

                IsQueued = context.QueueItems.Any(q => q.ProfileId == profileId && q.AnimeId == animeId)
            })
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new TitleDetail
        {
            AnimeId = row.AnimeId,
            Title = row.Title,
            MediaType = row.MediaType,
            EpisodeCount = row.EpisodeCount,
            EpisodeDurationMinutes = row.EpisodeDurationMinutes,
            ReleaseYear = row.ReleaseYear,
            Status = row.Status,
            EpisodesWatched = row.EpisodesWatched,
            UserScore = row.UserScore,
            IsQueued = row.IsQueued,
            Synopsis = row.Description,
            Genres = row.Genres,
            MainStudio = row.MainStudio,
            RecommendationScore = row.RecommendationScore,
            RecommendationConfidence = row.RecommendationConfidence,
            RecommendationReason = row.RecommendationReason,
            Source = row.Source,
            ExternalIds = [.. row.ExternalIds.Select(x => new ExternalIdentifier(x.Source, x.ExternalId))],
            PosterContentHash = row.PosterContentHash,
            PosterFileExtension = row.PosterFileExtension,
            ThumbnailContentHash = row.ThumbnailContentHash,
            ThumbnailFileExtension = row.ThumbnailFileExtension,
            CoverImageColor = row.CoverImageColor
        };
    }

    /// <summary>
    /// Aggregates over the library to discover which filters could match anything.
    ///
    /// Every value is computed by the database. Loading the library to inspect it
    /// would be the exact mistake this method exists to let the UI avoid.
    /// </summary>
    public async Task<LibraryFacets> GetFacetsAsync(int profileId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entries = context.LibraryEntries.AsNoTracking().Where(e => e.ProfileId == profileId);

        if (!await entries.AnyAsync(cancellationToken))
        {
            return LibraryFacets.Empty;
        }

        var mediaTypes = await entries
            .Where(e => e.Anime!.MediaType != MediaType.Unknown)
            .Select(e => e.Anime!.MediaType)
            .Distinct()
            .ToListAsync(cancellationToken);

        var years = await entries
            .Where(e => e.Anime!.ReleaseYear != null)
            .Select(e => e.Anime!.ReleaseYear!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Counted over identifiers so the chip offered matches the filter applied,
        // with Manual added by inversion for titles carrying none.
        var sources = await entries
            .SelectMany(e => e.Anime!.ExternalIds.Select(x => x.Source))
            .Distinct()
            .ToListAsync(cancellationToken);

        if (await entries.AnyAsync(e => !e.Anime!.ExternalIds.Any(), cancellationToken))
        {
            sources.Add(AnimeSource.Manual);
        }

        // Counted over exactly what the status options list. It used to exclude
        // hidden entries because the listing did too — a "Planning (8)" that
        // produced seven rows is a picker lying about its own options — and Phase
        // 18b removed both halves of that.
        var countByStatus = await entries
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        return new LibraryFacets
        {
            MediaTypes = [.. mediaTypes.OrderBy(m => m)],

            // Grouped here rather than in SQL: integer division translates
            // inconsistently across providers, and the distinct year list is tiny.
            Decades = [.. years.Select(y => y / 10 * 10).Distinct().Order()],

            Sources = [.. sources.OrderBy(s => s)],

            HasRuntimeData = await entries.AnyAsync(
                e => e.Anime!.EpisodeCount > 0 && e.Anime.EpisodeDurationMinutes > 0, cancellationToken),

            HasRecommendations = await entries.AnyAsync(e => e.RecommendationScore != null, cancellationToken),
            HasUnrankedEntries = await entries.AnyAsync(e => e.RecommendationScore == null, cancellationToken),
            HasUserScores = await entries.AnyAsync(e => e.UserScore != null, cancellationToken),

            // Asked of the graph rather than of the library, and not narrowed to
            // owned titles: it is the same population the filter reads, so the chip
            // appears exactly when pressing it could change the list.
            HasSequelEdges = await context.AnimeRelations.AnyAsync(
                r => r.RelationType == RelationType.Prequel || r.RelationType == RelationType.Sequel,
                cancellationToken),

            CountByStatus = countByStatus
        };
    }

    // No BulkUpdateAsync. It applied one edit to many entries inside a transaction,
    // reporting progress as it went, and hiding was the only edit that ever used it
    // — so Phase 18b took the last caller with it. D26 already removed the bulk
    // selection that would have given it more; if a filtered bulk action is ever
    // wanted, D26 records what it would take.

    private static IQueryable<LibraryEntry> ApplyFilters(
        AniQueueDbContext context,
        IQueryable<LibraryEntry> source,
        int profileId,
        LibraryQuery query)
    {
        var filtered = source.Where(e => e.ProfileId == profileId);

        if (query.Status is { } status)
        {
            filtered = filtered.Where(e => e.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            // Translated to SQL LIKE; SQLite's LIKE is case-insensitive for ASCII,
            // which is the behaviour a search box wants.
            //
            // Every variant is searched, not only the displayed one: someone reading
            // English titles still knows the show as Shingeki no Kyojin, and a search
            // box that cannot find it by the name they typed is the wrong answer (D22).
            filtered = filtered.Where(e =>
                EF.Functions.Like(e.Anime!.Title, $"%{term}%") ||
                (e.Anime!.TitleRomaji != null && EF.Functions.Like(e.Anime.TitleRomaji, $"%{term}%")) ||
                (e.Anime!.TitleEnglish != null && EF.Functions.Like(e.Anime.TitleEnglish, $"%{term}%")) ||
                (e.Anime!.TitleNative != null && EF.Functions.Like(e.Anime.TitleNative, $"%{term}%")));
        }

        if (query.MediaType is { } mediaType)
        {
            filtered = filtered.Where(e => e.Anime!.MediaType == mediaType);
        }

        if (query.Decade is { } decade)
        {
            filtered = filtered.Where(e =>
                e.Anime!.ReleaseYear >= decade && e.Anime.ReleaseYear < decade + 10);
        }

        if (query.MaxRuntimeMinutes is { } maxRuntime)
        {
            // Entries with no estimable runtime are excluded, not assumed short:
            // an unknown length is not evidence that something fits in an evening.
            filtered = filtered.Where(e =>
                e.Anime!.EpisodeCount != null
                && e.Anime.EpisodeDurationMinutes != null
                && e.Anime.EpisodeCount.Value * e.Anime.EpisodeDurationMinutes.Value <= maxRuntime);
        }

        if (query.Source is { } source1)
        {
            // "Is this title on that service", not "did that service create this
            // record" (D17). A hand-added title since linked to MyAnimeList belongs
            // under the MyAnimeList chip, which is what clicking it means.
            //
            // Manual keeps a useful meaning by inversion: carrying no external
            // identifier at all is exactly the hand-added set.
            filtered = source1 == AnimeSource.Manual
                ? filtered.Where(e => !e.Anime!.ExternalIds.Any())
                : filtered.Where(e => e.Anime!.ExternalIds.Any(x => x.Source == source1));
        }

        if (query.StandaloneOnly)
        {
            // An indexed EXISTS in both directions, which is the whole cost of it:
            // an edge is stored exactly as fetched (D24), so a title with a sequel
            // may be named at either end of the row that says so.
            //
            // Both halves of the OR are covered — the unique index leads on
            // ExternalId, the reverse index on RelatedExternalId — so this stays a
            // pair of index lookups rather than the scan the shape suggests.
            //
            // Counted over every edge rather than only owned ones, deliberately.
            // "Can I watch this on its own tonight" is a question about the show,
            // and answering it from what the library happens to contain would call a
            // series standalone until its second season was imported.
            filtered = filtered.Where(e => !e.Anime!.ExternalIds.Any(x =>
                x.Source == AnimeSource.AniList
                && context.AnimeRelations.Any(r =>
                    r.Source == AnimeSource.AniList
                    && (r.RelationType == RelationType.Prequel || r.RelationType == RelationType.Sequel)
                    && (r.ExternalId == x.ExternalId || r.RelatedExternalId == x.ExternalId))));
        }

        if (query.MinUserScore is { } minScore)
        {
            filtered = filtered.Where(e => e.UserScore >= minScore);
        }

        if (query.MinRecommendationConfidence is { } minConfidence)
        {
            filtered = filtered.Where(e => e.RecommendationConfidence >= minConfidence);
        }

        if (query.HasRecommendation is { } hasRecommendation)
        {
            filtered = hasRecommendation
                ? filtered.Where(e => e.RecommendationScore != null)
                : filtered.Where(e => e.RecommendationScore == null);
        }

        return filtered;
    }

    /// <summary>
    /// Applies the sort, always with a title tiebreak.
    ///
    /// Without one, entries sharing a sort key come back in whatever order SQLite
    /// happens to produce, which can differ between pages of the same result set —
    /// so a title can appear on two pages or none.
    /// </summary>
    private static IQueryable<LibraryEntry> ApplySort(IQueryable<LibraryEntry> source, LibrarySort sort) =>
        sort switch
        {
            LibrarySort.TitleDescending =>
                source.OrderByDescending(e => e.Anime!.Title),

            // Nulls last for every "best first" sort: an unranked or unknown entry
            // is not a good result, and placing it above real ones would be a lie.
            LibrarySort.RecommendationDescending =>
                source.OrderBy(e => e.RecommendationScore == null)
                      .ThenByDescending(e => e.RecommendationScore)
                      .ThenBy(e => e.Anime!.Title),

            LibrarySort.RuntimeAscending =>
                source.OrderBy(e => e.Anime!.EpisodeCount == null || e.Anime.EpisodeDurationMinutes == null)
                      .ThenBy(e => e.Anime!.EpisodeCount!.Value * e.Anime.EpisodeDurationMinutes!.Value)
                      .ThenBy(e => e.Anime!.Title),

            LibrarySort.RuntimeDescending =>
                source.OrderBy(e => e.Anime!.EpisodeCount == null || e.Anime.EpisodeDurationMinutes == null)
                      .ThenByDescending(e => e.Anime!.EpisodeCount!.Value * e.Anime.EpisodeDurationMinutes!.Value)
                      .ThenBy(e => e.Anime!.Title),

            LibrarySort.YearDescending =>
                source.OrderBy(e => e.Anime!.ReleaseYear == null)
                      .ThenByDescending(e => e.Anime!.ReleaseYear)
                      .ThenBy(e => e.Anime!.Title),

            LibrarySort.YearAscending =>
                source.OrderBy(e => e.Anime!.ReleaseYear == null)
                      .ThenBy(e => e.Anime!.ReleaseYear)
                      .ThenBy(e => e.Anime!.Title),

            LibrarySort.DateAddedDescending =>
                source.OrderByDescending(e => e.DateAdded).ThenBy(e => e.Anime!.Title),

            LibrarySort.UserScoreDescending =>
                source.OrderBy(e => e.UserScore == null)
                      .ThenByDescending(e => e.UserScore)
                      .ThenBy(e => e.Anime!.Title),

            _ => source.OrderBy(e => e.Anime!.Title)
        };
}

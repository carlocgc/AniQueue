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

        var filtered = ApplyFilters(context.LibraryEntries.AsNoTracking(), profileId, query);
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
                e.IsHidden,
                FranchiseName = e.Anime.Franchise != null ? e.Anime.Franchise.Name : null,
                e.RecommendationScore,
                e.RecommendationConfidence,
                e.Anime.Source,

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
                IsHidden = i.IsHidden,
                FranchiseName = i.FranchiseName,
                RecommendationScore = i.RecommendationScore,
                RecommendationConfidence = i.RecommendationConfidence,
                Source = i.Source,
                ExternalIds = [.. i.ExternalIds.Select(x => new ExternalIdentifier(x.Source, x.ExternalId))],
                IsQueued = queuedIds.Contains(i.AnimeId)
            }).ToList()
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
            HasFranchises = await entries.AnyAsync(e => e.Anime!.FranchiseId != null, cancellationToken),
            HasUserScores = await entries.AnyAsync(e => e.UserScore != null, cancellationToken),
            HasHiddenEntries = await entries.AnyAsync(e => e.IsHidden, cancellationToken),
            CountByStatus = countByStatus
        };
    }

    public Task<BulkActionResult> SetHiddenAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        bool hidden,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        BulkUpdateAsync(
            profileId,
            animeIds,
            entry => entry.IsHidden = hidden,
            hidden ? "Hiding entries" : "Restoring entries",
            progress,
            cancellationToken);

    private async Task<BulkActionResult> BulkUpdateAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        Action<LibraryEntry> update,
        string message,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(animeIds);

        if (animeIds.Count == 0)
        {
            return new BulkActionResult(0, 0);
        }

        progress?.Report(new OperationProgress(message));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var ids = animeIds.ToHashSet();

        var entries = await context.LibraryEntries
            .Where(e => e.ProfileId == profileId && ids.Contains(e.AnimeId))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var done = 0;

        foreach (var entry in entries)
        {
            update(entry);
            entry.LastUpdated = now;
            done++;

            progress?.Report(new OperationProgress(message, done, entries.Count));
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Bulk update '{Action}' applied to {Affected} of {Requested} requested entries",
            message,
            entries.Count,
            animeIds.Count);

        return new BulkActionResult(entries.Count, animeIds.Count - entries.Count);
    }

    private static IQueryable<LibraryEntry> ApplyFilters(
        IQueryable<LibraryEntry> source,
        int profileId,
        LibraryQuery query)
    {
        var filtered = source.Where(e => e.ProfileId == profileId);

        if (!query.IncludeHidden)
        {
            filtered = filtered.Where(e => !e.IsHidden);
        }

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

        filtered = query.Franchise switch
        {
            FranchiseFilter.InFranchise => filtered.Where(e => e.Anime!.FranchiseId != null),
            FranchiseFilter.Standalone => filtered.Where(e => e.Anime!.FranchiseId == null),
            _ => filtered
        };

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

using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Library;

public sealed class LibraryService(IDbContextFactory<AniQueueDbContext> contextFactory) : ILibraryService
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

        return new LibrarySummary
        {
            Total = counts.Values.Sum(),
            ByStatus = counts
        };
    }

    public async Task<LibraryPage> GetPageAsync(
        int profileId,
        LibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var filtered = context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId);

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

            // EF translates this to SQL LIKE; SQLite's LIKE is case-insensitive for
            // ASCII by default, which is the behaviour a search box wants.
            filtered = filtered.Where(e =>
                EF.Functions.Like(e.Anime!.Title, $"%{term}%") ||
                (e.Anime!.AlternativeTitle != null && EF.Functions.Like(e.Anime.AlternativeTitle, $"%{term}%")));
        }

        var total = await filtered.CountAsync(cancellationToken);

        var items = await filtered
            .OrderBy(e => e.Anime!.Title)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(e => new LibraryListItem
            {
                AnimeId = e.AnimeId,
                Title = e.Anime!.Title,
                MediaType = e.Anime.MediaType,
                EpisodeCount = e.Anime.EpisodeCount,
                ReleaseYear = e.Anime.ReleaseYear,
                Status = e.Status,
                EpisodesWatched = e.EpisodesWatched,
                UserScore = e.UserScore,
                FranchiseName = e.Anime.Franchise != null ? e.Anime.Franchise.Name : null,
                RecommendationScore = e.RecommendationScore
            })
            .ToListAsync(cancellationToken);

        return new LibraryPage { Items = items, TotalCount = total };
    }
}

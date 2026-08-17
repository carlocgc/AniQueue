using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Persistence.Seeding;

/// <summary>
/// Populates a development database with enough data to exercise every concept:
/// completed titles with a spread of scores, a title in progress, planning
/// entries, a franchise containing optional side entries, a manually ordered
/// queue mixing a franchise with standalone titles, and an applied AI ranking.
///
/// This is never invoked automatically. The caller decides, and the only caller
/// is guarded by a development-environment check — production databases are
/// never seeded (brief §34).
///
/// Seeding is idempotent: it does nothing if any title already exists.
/// </summary>
public sealed class DevelopmentSeeder(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ILogger<DevelopmentSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (await context.Anime.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Development seed skipped; the library already contains titles");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var profileId = Profile.DefaultProfileId;

        // A franchise whose later entries are optional, so franchise completion and
        // remaining-runtime maths have something meaningful to work against.
        var slayers = new Franchise
        {
            Name = "Slayers",
            Description = "Comedic sword-and-sorcery series, plus films and specials.",
            ManualSortOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        var slayersEntries = new[]
        {
            NewAnime("Slayers", MediaType.Tv, 26, 24, 1995, order: 1),
            NewAnime("Slayers Next", MediaType.Tv, 26, 24, 1996, order: 2),
            NewAnime("Slayers Try", MediaType.Tv, 26, 24, 1997, order: 3),
            NewAnime("Slayers: The Motion Picture", MediaType.Movie, 1, 75, 1995, order: 4, optional: true),
            NewAnime("Slayers Special", MediaType.Ova, 3, 30, 1996, order: 5, optional: true)
        };

        foreach (var entry in slayersEntries)
        {
            entry.Franchise = slayers;
        }

        // Completed, with a deliberate spread of scores — recommendation quality
        // depends on the model seeing both what the user liked and what they did not.
        var goldenBoy = NewAnime("Golden Boy", MediaType.Ova, 6, 25, 1995, source: AnimeSource.MyAnimeList, sourceId: "268");
        var gunbuster = NewAnime("Gunbuster", MediaType.Ova, 6, 30, 1988, source: AnimeSource.MyAnimeList, sourceId: "1953");
        var nichijou = NewAnime("Nichijou", MediaType.Tv, 26, 24, 2011, source: AnimeSource.MyAnimeList, sourceId: "10165");
        var konosuba = NewAnime("KonoSuba", MediaType.Tv, 10, 24, 2016, source: AnimeSource.MyAnimeList, sourceId: "30831");
        var mediocre = NewAnime("Najica Blitz Tactics", MediaType.Tv, 12, 24, 2001, source: AnimeSource.MyAnimeList, sourceId: "708");

        // In progress.
        var newGame = NewAnime("New Game!", MediaType.Tv, 12, 24, 2016, source: AnimeSource.MyAnimeList, sourceId: "31953");

        // Backlog.
        var hinamatsuri = NewAnime("Hinamatsuri", MediaType.Tv, 12, 24, 2018, source: AnimeSource.MyAnimeList, sourceId: "36296");
        var dragonMaid = NewAnime("Miss Kobayashi's Dragon Maid", MediaType.Tv, 13, 24, 2017, source: AnimeSource.MyAnimeList, sourceId: "33206");
        var unknownRuntime = NewAnime("Serial Experiments Lain", MediaType.Tv, 13, null, 1998, source: AnimeSource.MyAnimeList, sourceId: "339");

        context.Franchises.Add(slayers);
        context.Anime.AddRange(slayersEntries);
        context.Anime.AddRange(
            goldenBoy, gunbuster, nichijou, konosuba, mediocre,
            newGame, hinamatsuri, dragonMaid, unknownRuntime);

        await context.SaveChangesAsync(cancellationToken);

        context.LibraryEntries.AddRange(
            Completed(goldenBoy, score: 9),
            Completed(gunbuster, score: 10),
            Completed(nichijou, score: 9),
            Completed(konosuba, score: 8),
            Completed(mediocre, score: 4),
            Watching(newGame, episodesWatched: 4),
            Planning(hinamatsuri),
            Planning(dragonMaid),
            Planning(unknownRuntime));

        foreach (var entry in slayersEntries)
        {
            context.LibraryEntries.Add(Planning(entry));
        }

        // A hand-ordered queue with a standalone title deliberately sitting between
        // two seasons of the same franchise. That arrangement is the point of D15:
        // franchises group titles rather than occupying a slot, so the user can
        // space a long series out instead of committing to it in one block.
        context.QueueItems.AddRange(
            new QueueItem { ProfileId = profileId, Position = 0, AnimeId = hinamatsuri.Id, AddedAt = now },
            new QueueItem { ProfileId = profileId, Position = 1, AnimeId = slayersEntries[0].Id, AddedAt = now },
            new QueueItem { ProfileId = profileId, Position = 2, AnimeId = dragonMaid.Id, AddedAt = now },
            new QueueItem { ProfileId = profileId, Position = 3, AnimeId = slayersEntries[1].Id, AddedAt = now });

        var run = new RecommendationRun
        {
            ProfileId = profileId,
            CreatedAt = now,
            ProviderName = "ManualJson",
            ModelIdentifier = "sample-development-data",
            CompletedCount = 5,
            CandidateCount = 3,
            ResultCount = 3,
            WasApplied = true,
            Items =
            [
                RankItem(dragonMaid.Id, 1, 8.9, 0.86, "Comedy with a strong ensemble, matching high scores for Nichijou and KonoSuba."),
                RankItem(hinamatsuri.Id, 2, 8.4, 0.79, "Deadpan comedy in the vein of Nichijou."),
                RankItem(unknownRuntime.Id, 3, 6.2, 0.41, "Tonally distant from the user's comedy-weighted history.")
            ]
        };

        context.RecommendationRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);

        // Mirror the applied run onto the library entries, which is what the
        // backlog actually sorts on (D4).
        await ApplyRunToLibraryAsync(context, run, cancellationToken);

        logger.LogInformation(
            "Seeded development data: {AnimeCount} titles, 1 franchise, {QueueCount} queue entries",
            slayersEntries.Length + 9,
            3);

        LibraryEntry Completed(Anime anime, int score) => new()
        {
            ProfileId = profileId,
            AnimeId = anime.Id,
            Status = LibraryStatus.Completed,
            UserScore = score,
            EpisodesWatched = anime.EpisodeCount ?? 0,
            DateStarted = new DateOnly(2024, 1, 1),
            DateCompleted = new DateOnly(2024, 2, 1),
            DateAdded = now,
            LastUpdated = now
        };

        LibraryEntry Watching(Anime anime, int episodesWatched) => new()
        {
            ProfileId = profileId,
            AnimeId = anime.Id,
            Status = LibraryStatus.Watching,
            EpisodesWatched = episodesWatched,
            DateStarted = new DateOnly(2024, 6, 1),
            DateAdded = now,
            LastUpdated = now
        };

        LibraryEntry Planning(Anime anime) => new()
        {
            ProfileId = profileId,
            AnimeId = anime.Id,
            Status = LibraryStatus.Planning,
            DateAdded = now,
            LastUpdated = now
        };

        RecommendationRunItem RankItem(int animeId, int rank, double score, double confidence, string reason) => new()
        {
            AnimeId = animeId,
            Rank = rank,
            PredictedScore = score,
            Confidence = confidence,
            Reason = reason
        };

        Anime NewAnime(
            string title,
            MediaType mediaType,
            int? episodes,
            int? durationMinutes,
            int? year,
            int? order = null,
            bool optional = false,
            AnimeSource source = AnimeSource.Manual,
            string? sourceId = null) => new()
            {
                Title = title,
                MediaType = mediaType,
                EpisodeCount = episodes,
                EpisodeDurationMinutes = durationMinutes,
                ReleaseYear = year,
                FranchiseOrder = order,
                OptionalWithinFranchise = optional,
                Source = source,
                SourceAnimeId = sourceId,
                CreatedAt = now,
                UpdatedAt = now
            };
    }

    private static async Task ApplyRunToLibraryAsync(
        AniQueueDbContext context,
        RecommendationRun run,
        CancellationToken cancellationToken)
    {
        foreach (var item in run.Items.Where(i => i.AnimeId is not null))
        {
            var entry = await context.LibraryEntries
                .FirstOrDefaultAsync(e => e.AnimeId == item.AnimeId, cancellationToken);

            if (entry is null)
            {
                continue;
            }

            entry.RecommendationScore = item.PredictedScore;
            entry.RecommendationConfidence = item.Confidence;
            entry.RecommendationReason = item.Reason;
            entry.RecommendationUpdatedAt = run.CreatedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

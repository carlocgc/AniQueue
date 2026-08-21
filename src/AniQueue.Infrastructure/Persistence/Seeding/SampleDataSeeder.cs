using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Persistence.Seeding;

/// <summary>
/// Fills an empty database with enough data to exercise every concept: completed
/// titles with a spread of scores, a title in progress, planning entries, several
/// seasons of one series with the relation graph between them, a hidden entry, a
/// manually ordered queue with something deliberately sitting between two of those
/// seasons, and an applied AI ranking.
/// </summary>
/// <remarks>
/// <b>Only ever on request (D27).</b> This shipped as an automatic development
/// seeder and was deleted for it: sample titles carrying invented AniList
/// identifiers are indistinguishable, to D19's absence policy, from titles a source
/// has stopped listing, so the first real sync reported five of them as missing.
/// It is back because the alternative turned out to cost more — a queue crash went
/// unverified for want of any offline way to put rows in a queue — but it is back
/// behind two locks: the <c>SeedSampleData</c> switch, and a development-environment
/// check that stops production even resolving the type.
///
/// <b>Sample data and a real account are alternatives, not complements.</b> Seed a
/// database or sync one; a library holding both will report the sample titles as
/// absent, and that report is correct. To make the honest case easy, this leaves
/// AniList sync switched off in the database it seeds, so nothing unattended goes
/// looking. Pressing <i>Sync now</i> afterwards is a deliberate choice to mix them.
///
/// Idempotent: it does nothing if any title already exists.
/// </remarks>
public sealed class SampleDataSeeder(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ILogger<SampleDataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (await context.Anime.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Sample data skipped; the library already contains titles");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var profileId = Profile.DefaultProfileId;

        // Several seasons of one series, plus a film and an OVA. They are ordinary
        // titles like every other row here (D23) — what makes them a series is the
        // relations AniList publishes about them, not anything stored locally.
        //
        // Which is why they carry AniList identifiers: the relation graph is keyed
        // by them (D24), so without one there is nothing for the edges below to
        // attach to and the backlog's expansion cannot be seen in the inner loop at
        // all.
        //
        // **The identifiers are invented, and deliberately far outside the range
        // AniList issues.** A guessed-but-plausible id would be worse than an
        // obviously fake one: the row links out to AniList, and a link that lands
        // confidently on somebody else's show is a bug that looks like data.
        var slayersEntries = new[]
        {
            NewAnime("Slayers", MediaType.Tv, 26, 24, 1995,
                AnimeSource.AniList, "900001", new DateOnly(1995, 4, 7)),
            NewAnime("Slayers Next", MediaType.Tv, 26, 24, 1996,
                AnimeSource.AniList, "900002", new DateOnly(1996, 4, 5)),
            NewAnime("Slayers Try", MediaType.Tv, 26, 24, 1997,
                AnimeSource.AniList, "900003", new DateOnly(1997, 4, 4)),
            NewAnime("Slayers: The Motion Picture", MediaType.Movie, 1, 75, 1995,
                AnimeSource.AniList, "900004", new DateOnly(1995, 7, 15)),
            NewAnime("Slayers Special", MediaType.Ova, 3, 30, 1996,
                AnimeSource.AniList, "900005", new DateOnly(1996, 6, 25))
        };

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

        // Set aside, so the hidden view has something in it and the status picker
        // offers it. Sample data exists to make surfaces reachable, and a surface
        // reachable only after hiding something by hand is one nobody checks.
        var setAside = NewAnime("Mars of Destruction", MediaType.Ova, 1, 19, 2005, source: AnimeSource.MyAnimeList, sourceId: "413");

        context.Anime.AddRange(slayersEntries);
        context.Anime.AddRange(
            goldenBoy, gunbuster, nichijou, konosuba, mediocre,
            newGame, hinamatsuri, dragonMaid, unknownRuntime, setAside);

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
            Planning(unknownRuntime),
            Hidden(setAside));

        foreach (var entry in slayersEntries)
        {
            context.LibraryEntries.Add(Planning(entry));
        }

        // The graph the relation backfill would have written, seeded so the backlog
        // has something to expand without anyone having to sync a real account.
        //
        // Written the way the source states them rather than tidied into one
        // direction (D24), and the untidiness is the point: the edge from Try is
        // stored *only* from Try's side, so Next finds its own sequel through the
        // reverse index and inverts it. That path carries half a real graph and is
        // the half a hand-written seed would otherwise never exercise.
        context.AnimeRelations.AddRange(
            Edge("900001", RelationType.Sequel, "900002"),
            Edge("900003", RelationType.Prequel, "900002"),
            Edge("900001", RelationType.SideStory, "900004"),
            Edge("900005", RelationType.Parent, "900001"));

        // Sample data and a real account are alternatives, not complements: a library
        // holding both reports the sample titles as absent the first time a real list
        // comes back without them, and that report is correct (D19). So a seeded
        // database says up front that this source is not to be read, and nothing
        // unattended goes looking. Pressing Sync now afterwards is the user choosing
        // to mix the two, which is a decision rather than an accident.
        context.SourceSyncSettings.Add(new SourceSyncSettings
        {
            ProfileId = profileId,
            Source = AnimeSource.AniList,
            IsEnabled = false
        });

        // A hand-ordered queue with an unrelated title deliberately sitting between
        // two seasons of the same series. That arrangement is the point of D15: a
        // slot is one title, so the user can space a long series out instead of
        // committing to it in one block.
        context.QueueItems.AddRange(
            new QueueItem { ProfileId = profileId, Position = 0, AnimeId = hinamatsuri.Id, AddedAt = now },
            new QueueItem { ProfileId = profileId, Position = 1, AnimeId = slayersEntries[0].Id, AddedAt = now },
            new QueueItem { ProfileId = profileId, Position = 2, AnimeId = dragonMaid.Id, AddedAt = now },
            new QueueItem { ProfileId = profileId, Position = 3, AnimeId = slayersEntries[1].Id, AddedAt = now });

        var run = new RecommendationRun
        {
            ProfileId = profileId,
            CreatedAt = now,
            // What the Recommendations page actually writes. It said "ManualJson" —
            // the name of a class, in a column the backlog shows to a user — which was
            // invisible while the field was only a fallback for a missing model name
            // and became a leak the moment the route earned its own line.
            ProviderName = "Manual",
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
            "Seeded sample data: {AnimeCount} titles, {QueueCount} queue entries",
            slayersEntries.Length + 10,
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

        LibraryEntry Hidden(Anime anime) => new()
        {
            ProfileId = profileId,
            AnimeId = anime.Id,
            Status = LibraryStatus.Planning,
            IsHidden = true,
            DateAdded = now,
            LastUpdated = now
        };

        static AnimeRelation Edge(string externalId, RelationType type, string relatedExternalId) => new()
        {
            Source = AnimeSource.AniList,
            ExternalId = externalId,
            RelationType = type,
            RelatedExternalId = relatedExternalId
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
            AnimeSource source = AnimeSource.Manual,
            string? sourceId = null,
            DateOnly? startDate = null) => new()
            {
                Title = title,
                MediaType = mediaType,
                EpisodeCount = episodes,
                EpisodeDurationMinutes = durationMinutes,
                ReleaseYear = year,

                // Written for the titles that are related to something, because that
                // is the only place it is read: an expansion orders by air date, and
                // a year cannot separate two halves of a split cour (D24).
                StartDate = startDate,
                Source = source,
                ExternalIds = sourceId is null
                    ? []
                    : [new AnimeExternalId
                        {
                            Source = source,
                            ExternalId = sourceId,

                            // Seeded as already asked about, so the relation backfill
                            // stays idle. Without this every F5 would spend real
                            // requests against a real rate limit asking AniList about
                            // identifiers this file invented — and the answer would be
                            // silence, every time.
                            RelationsFetchedAt = source == AnimeSource.AniList ? now.UtcDateTime : null
                        }],
                CreatedAt = now,
                UpdatedAt = now
            };
    }

    private static async Task ApplyRunToLibraryAsync(
        AniQueueDbContext context,
        RecommendationRun run,
        CancellationToken cancellationToken)
    {
        // Every item is a title, so there is nothing to filter out. This used to skip
        // items with no AnimeId — the group case, and the fact that applying a run
        // had to discard those is what D16 acted on.
        foreach (var item in run.Items)
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

using AniQueue.Core.Domain;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Recommendations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// Phase 7's two halves against a real schema: what a model is given, and what
/// applying its answer is allowed to touch.
///
/// The second half carries most of the weight. A ranking is the one thing in
/// AniQueue written by something that is not the user, so the tests that matter
/// are the ones asserting what it leaves alone — status, progress, the user's own
/// score, and above all the queue order D11 says the user owns.
/// </summary>
public class RecommendationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A clock that does not move, so "when was this scored" is an assertion rather
    /// than a tolerance.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than taken from a testing package, the same way the
    /// relation backfill's tests do it: one overridden method is cheaper than a
    /// dependency, and this needs nothing else.
    /// </remarks>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

        public required IRecommendationService Recommendations { get; init; }

        public required int ProfileId { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var database = await SqliteTestDatabase.CreateAsync();

            await new DatabaseInitializer(
                database.ContextFactory,
                Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
                NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

            await using var context = database.CreateContext();
            var profile = await context.Profiles.FirstAsync();

            return new Fixture
            {
                Database = database,
                ProfileId = profile.Id,
                Recommendations = new RecommendationService(
                    database.ContextFactory,
                    new ScoringResponseParser(),
                    NullLogger<RecommendationService>.Instance,
                    new FixedTimeProvider(Now))
            };
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private static async Task<Anime> AddAsync(
        AniQueueDbContext context,
        int profileId,
        string title,
        LibraryStatus status = LibraryStatus.Planning,
        int? userScore = null,
        DateOnly? completed = null,
        bool hidden = false,
        string? notes = null,
        string? aniListId = null,
        string? englishTitle = null,
        MediaType mediaType = MediaType.Tv,
        int? episodes = 12,
        int? duration = 24,
        int? year = 2010)
    {
        var now = DateTimeOffset.UtcNow;

        var anime = new Anime
        {
            Title = title,
            TitleEnglish = englishTitle,
            MediaType = mediaType,
            EpisodeCount = episodes,
            EpisodeDurationMinutes = duration,
            ReleaseYear = year,
            Source = AnimeSource.AniList,
            ExternalIds = aniListId is null
                ? []
                : [new AnimeExternalId { Source = AnimeSource.AniList, ExternalId = aniListId }],
            CreatedAt = now,
            UpdatedAt = now
        };

        context.Anime.Add(anime);
        await context.SaveChangesAsync();

        context.LibraryEntries.Add(new LibraryEntry
        {
            ProfileId = profileId,
            AnimeId = anime.Id,
            Status = status,
            UserScore = userScore,
            DateCompleted = completed,
            IsHidden = hidden,
            PersonalNotes = notes,
            DateAdded = now,
            LastUpdated = now
        });

        await context.SaveChangesAsync();
        return anime;
    }

    private static string Ranking(params (int Id, int Rank, double Score)[] results) =>
        $$"""
          {
            "aniqueue": { "format": "aniqueue-scoring-response", "version": 1 },
            "results": [
              {{string.Join(",\n    ", results.Select(r =>
                  $$"""{ "id": {{r.Id}}, "rank": {{r.Rank}}, "predictedScore": {{r.Score}}, "confidence": 0.8, "reason": "Because." }"""))}}
            ]
          }
          """;

    [Fact]
    public async Task A_request_offers_the_visible_backlog_and_nothing_else()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await AddAsync(context, fixture.ProfileId, "Watching", LibraryStatus.Watching);
        await AddAsync(context, fixture.ProfileId, "Finished", LibraryStatus.Completed, userScore: 8);

        // Hidden means the user has already said they do not want to see it, and a
        // ranking is a reason to see something.
        await AddAsync(context, fixture.ProfileId, "Set aside", hidden: true);

        var request = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId);

        Assert.Equal([waiting.Id], request.Candidates.Select(c => c.Id));
    }

    [Fact]
    public async Task A_request_carries_what_a_model_needs_to_recognise_a_title()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(
            context,
            fixture.ProfileId,
            "Sousou no Frieren",
            englishTitle: "Frieren: Beyond Journey's End",
            aniListId: "154587",
            episodes: 28,
            duration: 24,
            year: 2023);

        var candidate = (await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId)).Candidates.Single();

        Assert.Equal("Sousou no Frieren", candidate.Title);
        Assert.Equal("Frieren: Beyond Journey's End", candidate.Titles.English);
        Assert.Equal("154587", candidate.ExternalIds.AniList);
        Assert.Equal(28, candidate.Episodes);
        Assert.Equal(2023, candidate.Year);
    }

    [Fact]
    public async Task History_is_the_users_own_scores()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(context, fixture.ProfileId, "Loved", LibraryStatus.Completed, userScore: 9);
        await AddAsync(context, fixture.ProfileId, "Disliked", LibraryStatus.Completed, userScore: 2);

        // Finished but never rated says nothing about taste, so it is not history.
        await AddAsync(context, fixture.ProfileId, "Unrated", LibraryStatus.Completed);

        var request = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId);

        Assert.Equal(2, request.History.Count);
        Assert.Equal(2, request.HistoryAvailable);
        Assert.False(request.IsHistoryCapped);
        Assert.Contains(request.History, h => h is { Title: "Disliked", Score: 2 });
    }

    [Fact]
    public async Task History_is_capped_at_the_most_recently_finished_and_says_so()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var year = 2000; year < 2005; year++)
        {
            await AddAsync(
                context,
                fixture.ProfileId,
                $"Finished in {year}",
                LibraryStatus.Completed,
                userScore: 7,
                completed: new DateOnly(year, 1, 1));
        }

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { MaxHistory = 2 });

        // The cap is visible rather than silent: a user comparing this against their
        // completed count has to be able to tell a sample from a bug.
        Assert.Equal(5, request.HistoryAvailable);
        Assert.True(request.IsHistoryCapped);
        Assert.Equal(["Finished in 2004", "Finished in 2003"], request.History.Select(h => h.Title));
    }

    [Fact]
    public async Task Notes_travel_only_when_they_were_opted_in()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(context, fixture.ProfileId, "Recommended", notes: "Ben says start here");

        Assert.Null((await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId)).Candidates.Single().Notes);

        var settings = await context.ProfileSettings.FirstAsync(s => s.ProfileId == fixture.ProfileId);
        settings.IncludePersonalNotesInAiExport = true;
        await context.SaveChangesAsync();

        Assert.Equal(
            "Ben says start here",
            (await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId)).Candidates.Single().Notes);
    }

    [Fact]
    public async Task An_id_naming_nothing_is_an_error_and_stops_everything()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var known = await AddAsync(context, fixture.ProfileId, "Known");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((known.Id, 1, 8.0), (9999, 2, 7.0)));

        Assert.True(preview.HasErrors);
        Assert.False(preview.CanApply);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual"));
    }

    [Fact]
    public async Task A_title_that_has_left_the_backlog_is_skipped_rather_than_scored()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Still waiting");
        var started = await AddAsync(context, fixture.ProfileId, "Started since", LibraryStatus.Watching);

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((waiting.Id, 1, 8.0), (started.Id, 2, 7.0)));

        Assert.False(preview.HasErrors);
        Assert.Equal(1, preview.ApplicableCount);
        Assert.Equal(1, preview.StaleCount);
        Assert.Contains(preview.Problems, p => p.Severity == ScoringSeverity.Warning);

        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");

        await using var check = fixture.Database.CreateContext();
        var entry = await check.LibraryEntries.SingleAsync(e => e.AnimeId == started.Id);
        Assert.Null(entry.RecommendationScore);
    }

    [Fact]
    public async Task Candidates_the_ranking_ignored_are_a_warning_and_keep_their_scores()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var ranked = await AddAsync(context, fixture.ProfileId, "Ranked");
        await AddAsync(context, fixture.ProfileId, "Ignored");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((ranked.Id, 1, 8.0)));

        // The case a small model produces constantly. Discarding a valid ranking of
        // 170 titles because twelve were omitted protects nothing.
        Assert.False(preview.HasErrors);
        Assert.True(preview.CanApply);
        Assert.Equal(1, preview.MissingCount);
        Assert.Contains(preview.Problems, p => p.Message.Contains("not ranked"));

        var result = await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");
        Assert.Equal(1, result.Applied);
    }

    [Fact]
    public async Task Applying_writes_the_score_the_run_and_its_items()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "First");
        var second = await AddAsync(context, fixture.ProfileId, "Second");
        await AddAsync(context, fixture.ProfileId, "Rated", LibraryStatus.Completed, userScore: 8);

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((first.Id, 1, 8.6), (second.Id, 2, 7.1)));

        var applied = await fixture.Recommendations.ApplyAsync(
            fixture.ProfileId,
            preview,
            "Manual",
            "some-local-model");

        Assert.Equal(2, applied.Applied);

        await using var check = fixture.Database.CreateContext();

        var entry = await check.LibraryEntries.SingleAsync(e => e.AnimeId == first.Id);
        Assert.Equal(8.6, entry.RecommendationScore);
        Assert.Equal(0.8, entry.RecommendationConfidence);
        Assert.Equal("Because.", entry.RecommendationReason);
        Assert.Equal(Now, entry.RecommendationUpdatedAt);

        var run = await check.RecommendationRuns.Include(r => r.Items).SingleAsync();
        Assert.Equal("Manual", run.ProviderName);
        Assert.Equal("some-local-model", run.ModelIdentifier);
        Assert.True(run.WasApplied);
        Assert.Equal(2, run.CandidateCount);
        Assert.Equal(2, run.ResultCount);

        // How many scored titles informed it, which is what makes two runs
        // comparable when the library has grown between them.
        Assert.Equal(1, run.CompletedCount);
        Assert.Equal(2, run.Items.Count);
        Assert.Equal(1, run.Items.Single(i => i.AnimeId == first.Id).Rank);
    }

    [Fact]
    public async Task Applying_touches_nothing_the_user_owns()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "First", userScore: 6);
        var second = await AddAsync(context, fixture.ProfileId, "Second");

        // Queued in the opposite order to the ranking, which is the whole point: the
        // model proposes an order and the user owns one (D11). If applying a ranking
        // could reorder this, the application would have no answer to "why did my
        // queue change".
        context.QueueItems.AddRange(
            SeedData.QueueSlot(fixture.ProfileId, 0, second.Id),
            SeedData.QueueSlot(fixture.ProfileId, 1, first.Id));

        await context.SaveChangesAsync();

        var before = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == fixture.ProfileId)
            .Select(e => new { e.AnimeId, e.Status, e.EpisodesWatched, e.UserScore, e.LastUpdated })
            .ToListAsync();

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((first.Id, 1, 9.0), (second.Id, 2, 5.0)));

        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");

        await using var check = fixture.Database.CreateContext();

        var after = await check.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == fixture.ProfileId)
            .Select(e => new { e.AnimeId, e.Status, e.EpisodesWatched, e.UserScore, e.LastUpdated })
            .ToListAsync();

        Assert.Equal(before, after);

        var queue = await check.QueueItems
            .AsNoTracking()
            .OrderBy(q => q.Position)
            .Select(q => q.AnimeId)
            .ToListAsync();

        Assert.Equal([second.Id, first.Id], queue);
    }

    [Fact]
    public async Task A_later_ranking_replaces_an_earlier_one_on_the_entry_and_both_runs_survive()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Reconsidered");

        var first = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, Ranking((anime.Id, 1, 4.0)));
        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, first, "Manual");

        var second = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, Ranking((anime.Id, 1, 9.0)));

        // The preview shows what would be replaced, so the change is visible before
        // it happens rather than after.
        Assert.Equal(4.0, second.Items.Single().PreviousScore);

        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, second, "Manual");

        await using var check = fixture.Database.CreateContext();

        Assert.Equal(9.0, (await check.LibraryEntries.SingleAsync(e => e.AnimeId == anime.Id)).RecommendationScore);

        // Denormalised for sorting, not instead of history (D4).
        Assert.Equal(2, await check.RecommendationRuns.CountAsync());
    }

    [Fact]
    public async Task Run_history_reads_newest_first()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Ranked twice");

        foreach (var score in new[] { 4.0, 9.0 })
        {
            var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, Ranking((anime.Id, 1, score)));
            await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");
        }

        // Ordered by key rather than CreatedAt, because SQLite cannot ORDER BY a
        // DateTimeOffset at all — it throws at query time. Reaching this assertion is
        // the test.
        var runs = await fixture.Recommendations.GetRunsAsync(fixture.ProfileId);

        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Id > runs[1].Id);
    }

    [Fact]
    public async Task A_ranking_of_another_profiles_library_cannot_be_applied_here()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var other = await SeedData.CreateProfileAsync(context, "Someone else");
        var theirs = await AddAsync(context, other.Id, "Their backlog");

        var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, Ranking((theirs.Id, 1, 8.0)));

        Assert.True(preview.HasErrors);
    }
}

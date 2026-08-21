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

    /// <summary>Marks an entry as scored at a given moment, without a whole run.</summary>
    private static async Task ScoreAsync(AniQueueDbContext context, int animeId, DateTimeOffset when)
    {
        var entry = await context.LibraryEntries.SingleAsync(e => e.AnimeId == animeId);

        entry.RecommendationScore = 7.0;
        entry.RecommendationUpdatedAt = when;

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A stand-in for the request a reply is being checked against, when the test
    /// cares only about which ids were offered and how many were asked for.
    /// </summary>
    private static ScoringRequest Asked(params int[] animeIds) => new()
    {
        GeneratedAt = Now,
        Candidates = animeIds.Select(id => new ScoringCandidate { Id = id, Title = $"#{id}" }).ToList()
    };

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
    public async Task A_capped_request_takes_the_titles_longest_without_a_score()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var never = await AddAsync(context, fixture.ProfileId, "Never scored");
        var recent = await AddAsync(context, fixture.ProfileId, "Aardvark, scored yesterday");
        var stale = await AddAsync(context, fixture.ProfileId, "Zebra, scored long ago");

        await ScoreAsync(context, recent.Id, Now.AddDays(-1));
        await ScoreAsync(context, stale.Id, Now.AddYears(-1));

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { MaxCandidates = 2 });

        // Alphabetically this would be Aardvark and Never; by staleness it is the
        // unscored one and the year-old one. Which is the whole point: a cap that took
        // the front of the alphabet would leave the back of the library unranked
        // however many times it was run.
        Assert.Equal([never.Id, stale.Id], request.Candidates.Select(c => c.Id).Order());

        // Still alphabetical on the wire — the selection decides what is in the
        // payload, not what order a person reads it in.
        Assert.Equal("Never scored", request.Candidates[0].Title);
        Assert.Equal("Zebra, scored long ago", request.Candidates[1].Title);
    }

    [Fact]
    public async Task A_capped_request_says_how_much_it_left_out()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 5; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Waiting {i}");
        }

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { MaxCandidates = 2 });

        Assert.Equal(2, request.Candidates.Count);
        Assert.Equal(5, request.CandidatesAvailable);
        Assert.True(request.IsCandidatesCapped);
    }

    [Fact]
    public async Task Repeated_capped_requests_sweep_the_backlog()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 6; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Waiting {i}");
        }

        var options = new ScoringRequestOptions { MaxCandidates = 2 };
        var seen = new List<int>();

        // Three runs of two, applying each, should cover all six exactly once. This is
        // the property that makes a cap a page size rather than a horizon.
        for (var round = 0; round < 3; round++)
        {
            var request = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId, options);
            var ids = request.Candidates.Select(c => c.Id).ToList();

            seen.AddRange(ids);

            var ranking = Ranking(ids.Select((id, index) => (id, index + 1, 7.0)).ToArray());
            var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ranking, request);

            await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");
        }

        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task An_uncapped_request_offers_everything_in_title_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(context, fixture.ProfileId, "Zebra");
        await AddAsync(context, fixture.ProfileId, "Aardvark");

        var request = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId);

        Assert.Equal(["Aardvark", "Zebra"], request.Candidates.Select(c => c.Title));
        Assert.False(request.IsCandidatesCapped);
    }

    [Fact]
    public async Task A_ranking_of_a_title_the_request_did_not_offer_is_skipped()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var offered = await AddAsync(context, fixture.ProfileId, "Offered");
        var notOffered = await AddAsync(context, fixture.ProfileId, "Held back by the cap");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((offered.Id, 1, 8.0), (notOffered.Id, 2, 7.0)),
            Asked(offered.Id));

        // Waiting, and real, but not part of the question — so its score was not
        // computed against the same set as the rest. A warning rather than an error:
        // the ranking of what was asked for is unaffected.
        Assert.False(preview.HasErrors);
        Assert.Equal(1, preview.ApplicableCount);
        Assert.Contains(preview.Items, i => i.SkippedBecause == "was not part of this request");

        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");

        await using var check = fixture.Database.CreateContext();
        Assert.Null((await check.LibraryEntries.SingleAsync(e => e.AnimeId == notOffered.Id)).RecommendationScore);
    }

    [Fact]
    public async Task A_capped_request_does_not_report_the_rest_of_the_backlog_as_missing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var offered = await AddAsync(context, fixture.ProfileId, "Offered");

        for (var i = 0; i < 4; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Not offered {i}");
        }

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((offered.Id, 1, 8.0)),
            Asked(offered.Id));

        // Without the offered set this would say four of five were not ranked, which
        // would turn the user's own candidate limit into a warning against itself.
        Assert.Equal(1, preview.CandidateCount);
        Assert.Equal(0, preview.MissingCount);
        Assert.DoesNotContain(preview.Problems, p => p.Message.Contains("did not come back"));
    }

    [Fact]
    public async Task Asking_for_the_top_few_narrows_the_reply_and_not_the_question()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 5; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Waiting {i}");
        }

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { ReturnTop = 2 });

        // Every title still goes: this bounds what comes back, not what is weighed.
        // Sending fewer titles and asking for fewer rankings are different questions,
        // and the second gets a better answer.
        Assert.Equal(5, request.Candidates.Count);
        Assert.Equal(2, request.ExpectedResults);
        Assert.True(request.IsRankingLimited);
        Assert.False(request.IsCandidatesCapped);
    }

    [Fact]
    public async Task A_reply_of_exactly_what_was_asked_for_is_complete()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "First");
        await AddAsync(context, fixture.ProfileId, "Second");
        await AddAsync(context, fixture.ProfileId, "Third");

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { ReturnTop = 1 });

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((first.Id, 1, 8.0)),
            request);

        // One of three ranked, and nothing missing — because one is what was asked
        // for. Measured against the candidates it would report two omissions the user
        // deliberately requested.
        Assert.Equal(3, preview.CandidateCount);
        Assert.Equal(1, preview.ExpectedCount);
        Assert.Equal(0, preview.MissingCount);
        Assert.DoesNotContain(preview.Problems, p => p.Message.Contains("did not come back"));
    }

    [Fact]
    public async Task A_reply_longer_than_what_was_asked_for_applies_and_says_so()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "First");
        var second = await AddAsync(context, fixture.ProfileId, "Second");
        var third = await AddAsync(context, fixture.ProfileId, "Third");

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { ReturnTop = 1 });

        // What a capable model actually does: asked for the best one, it ranks all
        // three. Everything it sent is valid, so all of it applies — but the setting
        // was ignored, and saying nothing would leave that looking like the setting
        // never worked.
        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((first.Id, 1, 9.0), (second.Id, 2, 8.0), (third.Id, 3, 7.0)),
            request);

        Assert.False(preview.HasErrors);
        Assert.Equal(3, preview.ApplicableCount);
        Assert.Contains(preview.Problems, p => p.Message.Contains("returned 3 rankings when 1 were asked for"));

        var applied = await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");
        Assert.Equal(3, applied.Applied);
    }

    [Fact]
    public async Task A_reply_shorter_than_what_was_asked_for_still_warns()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "First");
        await AddAsync(context, fixture.ProfileId, "Second");
        await AddAsync(context, fixture.ProfileId, "Third");

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { ReturnTop = 3 });

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((first.Id, 1, 8.0)),
            request);

        Assert.Equal(2, preview.MissingCount);
        Assert.Contains(preview.Problems, p => p.Message.Contains("2 of the 3 rankings"));
    }

    [Fact]
    public async Task What_a_request_carries_is_what_the_caller_asked_for()
    {
        // These sizes used to be read from ProfileSettings inside the service, which
        // is why there was once a test that they were "remembered". D36 moved them to
        // userconfig.json, so what is worth asserting here is that the service honours
        // what it is handed — where they were stored is UserSettingsStoreTests' problem.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 4; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Waiting {i}");
            await AddAsync(context, fixture.ProfileId, $"Rated {i}", LibraryStatus.Completed, userScore: 7);
        }

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { MaxHistory = 1, MaxCandidates = 2 });

        Assert.Equal(2, request.Candidates.Count);
        Assert.Single(request.History);

        // Still stated in full, so a capped request is visible as a sample rather than
        // as a smaller library.
        Assert.Equal(4, request.CandidatesAvailable);
        Assert.Equal(4, request.HistoryAvailable);
    }

    [Fact]
    public async Task A_caller_that_asks_for_nothing_gets_the_defaults()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(context, fixture.ProfileId, "Waiting");
        await AddAsync(context, fixture.ProfileId, "Rated", LibraryStatus.Completed, userScore: 8);

        var request = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId);

        Assert.Single(request.Candidates);
        Assert.Single(request.History);
        Assert.Null(request.ReturnTop);
    }

    [Fact]
    public async Task Sending_no_history_at_all_is_a_choice_that_survives()
    {
        // Zero is a real setting, not an absence: the ranking becomes general rather
        // than personal, and the user said so. It must not be read as "unset" and
        // quietly replaced by the default.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(context, fixture.ProfileId, "Rated", LibraryStatus.Completed, userScore: 8);
        await AddAsync(context, fixture.ProfileId, "Waiting");

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { MaxHistory = 0 });

        Assert.Empty(request.History);
        Assert.Equal(1, request.HistoryAvailable);
    }

    [Fact]
    public async Task Personal_notes_travel_only_when_the_caller_opts_in()
    {
        // §6's privacy rule, and the one setting whose default matters more than its
        // value: a caller that says nothing must get exclusion. The flag used to be a
        // ProfileSettings column read here; it now arrives with the options (D36), and
        // this is the assertion that the move did not turn "unset" into "included".
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var entry = await AddAsync(context, fixture.ProfileId, "Noted");
        var stored = await context.LibraryEntries.FirstAsync(e => e.AnimeId == entry.Id);
        stored.PersonalNotes = "Recommended by a friend";
        await context.SaveChangesAsync();

        var withheld = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId);
        Assert.Null(withheld.Candidates.Single().Notes);

        var shared = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { IncludePersonalNotes = true });

        Assert.Equal("Recommended by a friend", shared.Candidates.Single().Notes);
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
        Assert.Equal(1, preview.SkippedCount);
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
        Assert.Contains(preview.Problems, p => p.Message.Contains("did not come back"));

        var result = await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");
        Assert.Equal(1, result.Applied);
    }

    [Fact]
    public async Task How_long_a_run_took_is_recorded_and_read_back()
    {
        // The page quotes the last endpoint run to say how long a wait normally is,
        // which is the difference between a dialog that looks hung and one that looks
        // busy. It survives a restart because it is on the run rather than in the page.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Ranked");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((anime.Id, 1, 8.0)));

        await fixture.Recommendations.ApplyAsync(
            fixture.ProfileId,
            preview,
            "Remote",
            "qwen2.5-14b",
            progress: null,
            duration: TimeSpan.FromSeconds(374));

        var run = Assert.Single(await fixture.Recommendations.GetRunsAsync(fixture.ProfileId));

        Assert.Equal(TimeSpan.FromSeconds(374), run.Duration);
    }

    [Fact]
    public async Task A_run_nobody_timed_records_no_duration()
    {
        // The manual path, where the wait happened in somebody else's chat window and
        // AniQueue has no idea how long it was. Null rather than zero, because zero
        // would read as an instant answer.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Ranked");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            Ranking((anime.Id, 1, 8.0)));

        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");

        var run = Assert.Single(await fixture.Recommendations.GetRunsAsync(fixture.ProfileId));

        Assert.Null(run.Duration);
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
    public async Task A_score_can_say_where_it_came_from()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Explained");
        await AddAsync(context, fixture.ProfileId, "Also waiting");

        var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, Ranking((anime.Id, 1, 8.6)));
        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual", "some-local-model");

        var detail = await fixture.Recommendations.GetDetailAsync(fixture.ProfileId, anime.Id);

        Assert.NotNull(detail);
        Assert.Equal(1, detail.Rank);
        Assert.Equal(8.6, detail.PredictedScore);
        Assert.Equal(0.8, detail.Confidence);
        Assert.Equal("Because.", detail.Reason);
        Assert.Equal("Manual", detail.ProviderName);
        Assert.Equal("some-local-model", detail.ModelIdentifier);
        Assert.Equal(Now, detail.DeterminedAt);

        // How many titles were weighed to place it, which is what makes "ranked 1 of
        // 2" mean something.
        Assert.Equal(2, detail.CandidateCount);
    }

    [Fact]
    public async Task A_score_explains_itself_with_the_ranking_that_wrote_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Reconsidered");

        foreach (var score in new[] { 4.0, 9.0 })
        {
            var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, Ranking((anime.Id, 1, score)));
            await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual");
        }

        // The latest applied run, not the first. Ordered by run key rather than
        // CreatedAt, because SQLite cannot ORDER BY a DateTimeOffset and throws at
        // query time — reaching this assertion is half the test.
        var detail = await fixture.Recommendations.GetDetailAsync(fixture.ProfileId, anime.Id);

        Assert.Equal(9.0, detail!.PredictedScore);
    }

    [Fact]
    public async Task A_title_no_applied_ranking_mentions_has_nothing_to_explain()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Never ranked");

        Assert.Null(await fixture.Recommendations.GetDetailAsync(fixture.ProfileId, anime.Id));
    }

    [Fact]
    public async Task Another_profiles_ranking_does_not_explain_this_ones_score()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var other = await SeedData.CreateProfileAsync(context, "Someone else");
        var theirs = await AddAsync(context, other.Id, "Their title");

        Assert.Null(await fixture.Recommendations.GetDetailAsync(fixture.ProfileId, theirs.Id));
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

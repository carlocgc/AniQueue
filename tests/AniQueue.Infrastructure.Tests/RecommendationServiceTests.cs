using AniQueue.Core.Domain;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Recommendations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The two halves of the scoring contract against a real schema: what a model is given, and what
/// applying its answer is allowed to touch.
///
/// The second half carries most of the weight. A ranking is the one thing in
/// AniQueue written by something that is not the user, so the tests that matter
/// are the ones asserting what it leaves alone — status, progress, the user's own
/// score, and above all the queue order the user owns.
/// </summary>
public class RecommendationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The library key every fixture is forced to, so a reply naming it can be written
    /// as a literal.
    /// </summary>
    /// <remarks>
    /// Overwritten rather than read back, because the real one is random and a test that
    /// interpolated it could not show what a valid reply looks like.
    /// </remarks>
    private const string TestLibraryKey = "abcdef012345";

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

            profile.LibraryKey = TestLibraryKey;
            await context.SaveChangesAsync();

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

    /// <summary>A reply naming the fixture's own library, which is the ordinary case.</summary>
    private static string Ranking(params (int Id, double Score)[] results) =>
        RankingFrom(TestLibraryKey, results);

    /// <summary>A reply naming some other library, or none at all.</summary>
    private static string RankingFrom(string? library, params (int Id, double Score)[] results) =>
        $$"""
          {
            "aniqueue": { "format": "aniqueue-scoring-response", "version": 1{{Names(library)}} },
            "results": [
              {{string.Join(",\n    ", results.Select(r =>
                  $$"""{ "id": {{r.Id}}, "predictedScore": {{r.Score}}, "confidence": 0.8, "reason": "Because." }"""))}}
            ]
          }
          """;

    private static string Names(string? library) =>
        library is null ? string.Empty : $", \"library\": \"{library}\"";

    [Fact]
    public async Task A_request_offers_the_visible_backlog_and_nothing_else()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await AddAsync(context, fixture.ProfileId, "Watching", LibraryStatus.Watching);
        await AddAsync(context, fixture.ProfileId, "Finished", LibraryStatus.Completed, userScore: 8);

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
        Assert.Equal(2023, candidate.Year);
        Assert.Equal(MediaType.Tv, candidate.MediaType);
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

    /// <summary>
    /// A sweep hands back the candidates of a batch it could not score, and they take
    /// no further part in it.
    /// </summary>
    /// <remarks>
    /// Asserted against real SQLite rather than in the job's own tests, because the
    /// exclusion is a clause in the query that reads the backlog — and a clause EF
    /// cannot translate fails when it runs rather than when it is built.
    /// </remarks>
    [Fact]
    public async Task A_title_set_aside_is_left_out_of_the_next_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "Never scored, asked first");
        var second = await AddAsync(context, fixture.ProfileId, "Never scored, asked second");

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions
            {
                MaxCandidates = 1,
                ExcludeCandidates = new HashSet<int> { first.Id }
            });

        // The one it would have taken is gone, and the batch is still full: a set-aside
        // title does not take a place and then vanish from it.
        Assert.Equal([second.Id], request.Candidates.Select(c => c.Id));

        // And the backlog is still reported whole, because how many titles are waiting
        // is a fact about the library rather than about one request.
        Assert.Equal(2, request.CandidatesAvailable);
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

            var ranking = Ranking([.. ids.Select(id => (id, 7.0))]);
            var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, ranking, request);

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
            ScoringRoute.Pasted,
            Ranking((offered.Id, 8.0), (notOffered.Id, 7.0)),
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
            ScoringRoute.Pasted,
            Ranking((offered.Id, 8.0)),
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
            ScoringRoute.Pasted,
            Ranking((first.Id, 8.0)),
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
            ScoringRoute.Pasted,
            Ranking((first.Id, 9.0), (second.Id, 8.0), (third.Id, 7.0)),
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
            ScoringRoute.Pasted,
            Ranking((first.Id, 8.0)),
            request);

        Assert.Equal(2, preview.MissingCount);
        Assert.Contains(preview.Problems, p => p.Message.Contains("2 of the 3 rankings"));
    }

    [Fact]
    public async Task What_a_request_carries_is_what_the_caller_asked_for()
    {
        // The sizes live in userconfig.json, so what is worth asserting here is that
        // the service honours what it is handed. Where they are stored is
        // UserSettingsStoreTests' problem.
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
    public async Task An_unset_history_size_sends_every_rated_title()
    {
        // Null is a real setting rather than an absence, and it means all of them. An
        // empty field on the page means everything, not nothing — the two sizes beside
        // it read it the same way.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 3; i++)
        {
            await AddAsync(
                context, fixture.ProfileId, $"Rated {i}", LibraryStatus.Completed, userScore: 8);
        }

        await AddAsync(context, fixture.ProfileId, "Waiting");

        var request = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId,
            new ScoringRequestOptions { MaxHistory = null });

        Assert.Equal(3, request.History.Count);
        Assert.Equal(3, request.HistoryAvailable);
        Assert.False(request.IsHistoryCapped);
    }

    [Fact]
    public void A_stored_history_size_of_zero_becomes_one_rather_than_all()
    {
        // The upgrade path for a configuration file written when zero meant "send none".
        // It cannot keep meaning that, because the field that produced it now spells the
        // same intention by being empty — and reading it as all of them would silently
        // turn the smallest request somebody chose into the largest one there is.
        Assert.Equal(1, ScoringRequestOptions.From(0, candidateLimit: null).MaxHistory);
        Assert.Null(ScoringRequestOptions.From(null, candidateLimit: null).MaxHistory);
    }

    [Fact]
    public async Task Personal_notes_travel_only_when_the_caller_opts_in()
    {
        // The privacy rule, and the one setting whose default matters more than its
        // value: a caller that says nothing must get exclusion. The flag arrives with
        // the options, and this is the assertion that "unset" is never "included".
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
            ScoringRoute.Pasted,
            Ranking((known.Id, 8.0), (9999, 7.0)));

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
            ScoringRoute.Pasted,
            Ranking((waiting.Id, 8.0), (started.Id, 7.0)));

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
            ScoringRoute.Pasted,
            Ranking((ranked.Id, 8.0)));

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
            ScoringRoute.Pasted,
            Ranking((anime.Id, 8.0)));

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
            ScoringRoute.Pasted,
            Ranking((anime.Id, 8.0)));

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
            ScoringRoute.Pasted,
            Ranking((first.Id, 8.6), (second.Id, 7.1)));

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
        Assert.Equal(8.6, run.Items.Single(i => i.AnimeId == first.Id).PredictedScore);
    }

    [Fact]
    public async Task Applying_touches_nothing_the_user_owns()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var first = await AddAsync(context, fixture.ProfileId, "First", userScore: 6);
        var second = await AddAsync(context, fixture.ProfileId, "Second");

        // Queued in the opposite order to the ranking, which is the whole point: the
        // model proposes an order and the user owns one. If applying a ranking
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
            ScoringRoute.Pasted,
            Ranking((first.Id, 9.0), (second.Id, 5.0)));

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

        var first = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, Ranking((anime.Id, 4.0)));
        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, first, "Manual");

        var second = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, Ranking((anime.Id, 9.0)));

        // The preview shows what would be replaced, so the change is visible before
        // it happens rather than after.
        Assert.Equal(4.0, second.Items.Single().PreviousScore);

        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, second, "Manual");

        await using var check = fixture.Database.CreateContext();

        Assert.Equal(9.0, (await check.LibraryEntries.SingleAsync(e => e.AnimeId == anime.Id)).RecommendationScore);

        // Denormalised for sorting, not instead of history.
        Assert.Equal(2, await check.RecommendationRuns.CountAsync());
    }

    [Fact]
    public async Task A_score_can_say_where_it_came_from()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Explained");
        await AddAsync(context, fixture.ProfileId, "Also waiting");

        var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, Ranking((anime.Id, 8.6)));
        await fixture.Recommendations.ApplyAsync(fixture.ProfileId, preview, "Manual", "some-local-model");

        var detail = await fixture.Recommendations.GetDetailAsync(fixture.ProfileId, anime.Id);

        Assert.NotNull(detail);
        Assert.Equal(8.6, detail.PredictedScore);
        Assert.Equal(0.8, detail.Confidence);
        Assert.Equal("Because.", detail.Reason);
        Assert.Equal("Manual", detail.ProviderName);
        Assert.Equal("some-local-model", detail.ModelIdentifier);
        Assert.Equal(Now, detail.DeterminedAt);

        // Rank and CandidateCount were asserted here too, together rendering
        // "Ranked 1 of 2" on the title. Both are gone: the placement it reported
        // was relative to a batch the user never sees, and a score derived from a
        // placement is what the whole phase exists to stop.
    }

    [Fact]
    public async Task A_score_explains_itself_with_the_ranking_that_wrote_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Reconsidered");

        foreach (var score in new[] { 4.0, 9.0 })
        {
            var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, Ranking((anime.Id, score)));
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
            var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, Ranking((anime.Id, score)));
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

        var preview = await fixture.Recommendations.PreviewAsync(fixture.ProfileId, ScoringRoute.Pasted, Ranking((theirs.Id, 8.0)));

        Assert.True(preview.HasErrors);
    }

    // The read half, which the scoring card reports and the sweep
    // will pick its batches from. Both come from this one query so that what the page
    // says and what the job does cannot describe different backlogs.

    [Fact]
    public async Task Coverage_counts_what_is_waiting_ranked_and_never_ranked()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var ranked = await AddAsync(context, fixture.ProfileId, "Ranked");
        await AddAsync(context, fixture.ProfileId, "Never ranked");
        await AddAsync(context, fixture.ProfileId, "Watching", LibraryStatus.Watching);

        await ScoreAsync(context, ranked.Id, Now);

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5);

        // Non-Planning titles are not waiting, so they are not this card's business
        // — the same set a request is built from.
        Assert.Equal(2, coverage.Waiting);
        Assert.Equal(1, coverage.Ranked);
        Assert.Equal(1, coverage.Unranked);
        Assert.Equal(0, coverage.Stale);
    }

    [Fact]
    public async Task A_score_goes_stale_once_enough_further_titles_have_been_rated()
    {
        // The rule itself: a score is overtaken by ratings made after it, not by time.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await ScoreAsync(context, waiting.Id, Now.AddDays(-30));

        // Four finished and rated since. One short of the threshold, so nothing has
        // moved yet.
        for (var i = 0; i < 4; i++)
        {
            await AddAsync(
                context, fixture.ProfileId, $"Rated {i}", LibraryStatus.Completed,
                userScore: 8, completed: Day(1));
        }

        Assert.Equal(
            0,
            (await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5)).Stale);

        await AddAsync(
            context, fixture.ProfileId, "Rated 5", LibraryStatus.Completed,
            userScore: 9, completed: Day(1));

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5);

        Assert.Equal(1, coverage.Stale);
        Assert.Equal(0, coverage.UpToDate);
        Assert.False(coverage.IsSettled);
    }

    [Fact]
    public async Task A_score_made_after_the_ratings_is_not_stale()
    {
        // The other direction, and the one that makes the sweep terminate: re-scoring
        // a title has to actually settle it, or the job would pick the same rows
        // forever.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 6; i++)
        {
            await AddAsync(
                context, fixture.ProfileId, $"Rated {i}", LibraryStatus.Completed,
                userScore: 8, completed: Day(10));
        }

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await ScoreAsync(context, waiting.Id, Now);

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5);

        Assert.Equal(0, coverage.Stale);
        Assert.Equal(1, coverage.UpToDate);
        Assert.True(coverage.IsSettled);
    }

    /// <summary>
    /// A sync rewriting rated rows does not age a single score.
    /// </summary>
    /// <remarks>
    /// Found in a real library reporting its whole backlog out of date after the user
    /// had finished one title and rated none. The rule read <c>LastUpdated</c>, which
    /// is when the row was last saved by anything — so six rated titles touched by one
    /// sync put the line at that sync, ahead of every score in the library. What the
    /// page promises is titles finished and rated, and that is a date no sync writes.
    /// </remarks>
    [Fact]
    public async Task A_sync_rewriting_rated_titles_does_not_make_a_score_stale()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await ScoreAsync(context, waiting.Id, Now.AddDays(-1));

        // Six titles finished long before the score, so none of them overtakes it.
        for (var i = 0; i < 6; i++)
        {
            var rated = await AddAsync(
                context, fixture.ProfileId, $"Rated {i}", LibraryStatus.Completed,
                userScore: 8, completed: Day(400));

            // ...and all six rewritten just now, as one sync applying a change to each
            // of them would.
            await TouchAsync(context, rated.Id, Now);
        }

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5);

        Assert.Equal(0, coverage.Stale);
        Assert.Equal(1, coverage.UpToDate);
    }

    [Fact]
    public async Task Nothing_is_stale_until_enough_has_been_rated_to_make_it_so()
    {
        // A backlog scored against three ratings has not been overtaken by anything.
        // The right answer for a new library, and not a special case in the query.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await ScoreAsync(context, waiting.Id, Now.AddYears(-1));

        await AddAsync(context, fixture.ProfileId, "Rated", LibraryStatus.Completed, userScore: 8);

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5);

        Assert.Equal(0, coverage.Stale);
        Assert.True(coverage.IsSettled);
    }

    [Fact]
    public async Task A_threshold_of_zero_never_calls_anything_stale()
    {
        // Somebody who wants scores to stay put, which is a legitimate
        // choice rather than a misconfiguration.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var waiting = await AddAsync(context, fixture.ProfileId, "Waiting");
        await ScoreAsync(context, waiting.Id, Now.AddYears(-5));

        for (var i = 0; i < 20; i++)
        {
            await AddAsync(
                context, fixture.ProfileId, $"Rated {i}", LibraryStatus.Completed,
                userScore: 8, completed: Day(0));
        }

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 0);

        Assert.Equal(0, coverage.Stale);
        Assert.True(coverage.IsSettled);
    }

    [Fact]
    public async Task An_empty_backlog_is_neither_settled_nor_in_need_of_work()
    {
        // Nothing waiting means nothing to rank, and a green "all up to date" over an
        // empty backlog would be a claim about nothing. The card does not draw at all.
        await using var fixture = await Fixture.CreateAsync();

        var coverage = await fixture.Recommendations.GetCoverageAsync(fixture.ProfileId, staleAfterRatings: 5);

        Assert.Equal(0, coverage.Waiting);
        Assert.False(coverage.IsSettled);
        Assert.True(coverage.IsUntouched);
    }

    /// <summary>The day a title was finished, relative to the fixture's fixed clock.</summary>
    private static DateOnly Day(int daysAgo) => DateOnly.FromDateTime(Now.AddDays(-daysAgo).UtcDateTime);

    /// <summary>
    /// Rewrites an entry's last-updated stamp, the way any sync that touches the row
    /// does. It says when the row was saved and nothing about when it was rated.
    /// </summary>
    private static async Task TouchAsync(AniQueueDbContext context, int animeId, DateTimeOffset when)
    {
        var entry = await context.LibraryEntries.SingleAsync(e => e.AnimeId == animeId);

        entry.LastUpdated = when;

        await context.SaveChangesAsync();
    }
    [Fact]
    public async Task A_request_says_which_database_it_is_about()
    {
        // Without this in the envelope there is nothing for a reply to echo, and the
        // library-key check can never fire.
        await using var fixture = await Fixture.CreateAsync();

        var request = await fixture.Recommendations.BuildRequestAsync(fixture.ProfileId);

        Assert.Equal(TestLibraryKey, request.Library);
    }

    [Theory]
    [InlineData(ScoringRoute.Pasted)]
    [InlineData(ScoringRoute.Endpoint)]
    public async Task A_reply_naming_another_database_is_refused_whole(ScoringRoute route)
    {
        // The failure the key exists for, and the reason matching cannot catch it: every id
        // here names a real title in a real backlog. Only the envelope knows the reply
        // is about somewhere else.
        //
        // Refused on both routes. The endpoint is not asked to name a database, but an
        // endpoint that names the wrong one is telling us something, and there is no
        // reading of it that leaves the reply safe.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Waiting");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            route,
            RankingFrom("000000000000", (anime.Id, 8.0)));

        Assert.True(preview.HasErrors);
        Assert.Empty(preview.Items);

        // One problem, not one per result. A reply from elsewhere carries hundreds.
        var problem = Assert.Single(preview.Problems);
        Assert.Contains("different AniQueue database", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pasted_reply_naming_no_database_is_refused()
    {
        // A person carried this file, so
        // nothing here can establish that it belongs to this database, and every id in
        // it will name something whether it belongs or not.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Waiting");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            ScoringRoute.Pasted,
            RankingFrom(null, (anime.Id, 8.0)));

        Assert.True(preview.HasErrors);
        Assert.Empty(preview.Items);

        var problem = Assert.Single(preview.Problems);

        // The refusal has to be actionable, so it carries the line to add and the value
        // to put in it. A dead end would send the user back to the model with nothing
        // to change.
        Assert.Contains(TestLibraryKey, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_reply_naming_no_database_is_read_normally()
    {
        // The exemption, structural rather than a concession: this reply came back from
        // a request sent moments earlier in the same process, and the schema a
        // constrained server is given declares no envelope for it to answer in.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Waiting");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            ScoringRoute.Endpoint,
            RankingFrom(null, (anime.Id, 8.0)));

        Assert.False(preview.HasErrors);
        Assert.Equal(anime.Id, Assert.Single(preview.Items).Result.Id);
    }

    [Fact]
    public async Task A_reply_where_nothing_matches_is_reported_once()
    {
        // A reply that names this database and still matches none of it. Not the
        // provenance case, which is caught above, but a model inventing ids — which
        // produces the same wall of identical sentences and deserves the same summary.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        await AddAsync(context, fixture.ProfileId, "Waiting");

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            ScoringRoute.Pasted,
            Ranking((9001, 8.0), (9002, 7.0), (9003, 6.0)));

        Assert.True(preview.HasErrors);

        var problem = Assert.Single(preview.Problems);
        Assert.Contains("None of the 3 rankings", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unmatched_ids_are_summarised_once_there_are_too_many_to_read()
    {
        // What the user actually saw: a panel of identical red sentences, one per
        // result, with the button below it. The information is all still here — five
        // examples and a count — in six lines rather than eleven.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var anime = await AddAsync(context, fixture.ProfileId, "Waiting");

        var results = Enumerable.Range(9001, 10)
            .Select(id => (Id: id, Score: 7.0))
            .Append((anime.Id, 8.0))
            .ToArray();

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId, ScoringRoute.Pasted, Ranking(results));

        Assert.True(preview.HasErrors);

        Assert.Equal(6, preview.Problems.Count);
        Assert.Equal(
            5,
            preview.Problems.Count(p => p.Message.Contains("there is no title", StringComparison.Ordinal)));
        Assert.Contains(
            preview.Problems,
            p => p.Message.Contains("5 further result(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Titles_that_have_left_the_backlog_are_summarised_the_same_way()
    {
        // Found in the wild at twenty-four, burying the errors above it: a reply built
        // against a replaced database lands most of its ids on rows that were never
        // candidates, and most of those are not waiting to be watched.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        var moved = new List<int>();

        for (var i = 0; i < 8; i++)
        {
            var watched = await AddAsync(
                context, fixture.ProfileId, $"Started {i}", LibraryStatus.Watching);

            moved.Add(watched.Id);
        }

        var preview = await fixture.Recommendations.PreviewAsync(
            fixture.ProfileId,
            ScoringRoute.Pasted,
            Ranking([.. moved.Select(id => (id, 7.0))]));

        // Warnings, still: the ranking was right when it was made, and a title that has
        // left the backlog is news rather than a fault.
        Assert.False(preview.HasErrors);
        Assert.Equal(6, preview.Problems.Count);
        Assert.Contains(
            preview.Problems,
            p => p.Message.Contains("3 further title(s) are no longer waiting", StringComparison.Ordinal));
    }


    [Fact]
    public async Task A_size_estimate_reports_what_is_there_to_send()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 4; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Waiting {i}");
        }

        await AddAsync(context, fixture.ProfileId, "Rated", LibraryStatus.Completed, userScore: 8);

        var estimate = await fixture.Recommendations.MeasureAsync(fixture.ProfileId);

        Assert.Equal(4, estimate.CandidatesAvailable);
        Assert.Equal(1, estimate.HistoryAvailable);
        Assert.True(estimate.BaselineCharacters > 0);
        Assert.True(estimate.PerCandidateCharacters > 0);
    }

    [Fact]
    public async Task A_size_estimate_predicts_what_a_real_request_costs()
    {
        // The whole point of measuring rather than estimating, and the only assertion
        // that can catch the slope being wrong: a request of five identical-sized titles
        // must be four slopes longer than a request of one.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 5; i++)
        {
            await AddAsync(context, fixture.ProfileId, $"Waiting {i}", aniListId: $"1000{i}");
        }

        var estimate = await fixture.Recommendations.MeasureAsync(fixture.ProfileId);

        var one = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId, ScoringRequestOptions.From(null, candidateLimit: 1));

        var five = await fixture.Recommendations.BuildRequestAsync(
            fixture.ProfileId, ScoringRequestOptions.From(null, candidateLimit: 5));

        var actual = ScoringRequestWriter.Write(five).Length - ScoringRequestWriter.Write(one).Length;

        Assert.Equal(4 * estimate.PerCandidateCharacters, actual);
    }

    [Fact]
    public async Task A_size_estimate_counts_personal_notes_when_they_travel()
    {
        // The old pair of probes never passed this flag, so somebody who had opted in
        // was told their request was smaller than the one they would actually send —
        // on the one card whose number exists to warn about a model's context limit.
        await using var fixture = await Fixture.CreateAsync();
        await using var context = fixture.Database.CreateContext();

        for (var i = 0; i < 2; i++)
        {
            await AddAsync(
                context,
                fixture.ProfileId,
                $"Waiting {i}",
                notes: "Recommended by a friend who has never been wrong about this.");
        }

        var without = await fixture.Recommendations.MeasureAsync(fixture.ProfileId);

        var with = await fixture.Recommendations.MeasureAsync(
            fixture.ProfileId,
            ScoringRequestOptions.From(null, candidateLimit: null, includePersonalNotes: true));

        Assert.True(with.PerCandidateCharacters > without.PerCandidateCharacters);
    }
}

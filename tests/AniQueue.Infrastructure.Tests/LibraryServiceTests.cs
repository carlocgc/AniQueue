using System.Globalization;
using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Library;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

public class LibraryServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

        public required ILibraryService Library { get; init; }

        public required Core.Queue.IQueueService Queue { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var database = await SqliteTestDatabase.CreateAsync();

            await new DatabaseInitializer(
                database.ContextFactory,
                Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
                NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

            return new Fixture
            {
                Database = database,
                Library = new LibraryService(database.ContextFactory, NullLogger<LibraryService>.Instance),
                Queue = new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance)
            };
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private static async Task<Anime> AddAsync(
        AniQueueDbContext context,
        string title,
        LibraryStatus status = LibraryStatus.Planning,
        MediaType mediaType = MediaType.Tv,
        int? episodes = 12,
        int? duration = 24,
        int? year = 2010,
        int? userScore = null,
        double? recommendation = null,
        double? confidence = null,
        AnimeSource source = AnimeSource.MyAnimeList,
        string? sourceId = null)
    {
        var now = DateTimeOffset.UtcNow;

        var anime = new Anime
        {
            Title = title,
            MediaType = mediaType,
            EpisodeCount = episodes,
            EpisodeDurationMinutes = duration,
            ReleaseYear = year,
            Source = source,

            // A distinct identifier per title unless one is given, so the uniqueness
            // index does not make unrelated tests collide. Numeric because
            // SourceLinkBuilder refuses anything else.
            ExternalIds =
            [
                new AnimeExternalId
                {
                    Source = source,
                    ExternalId = sourceId
                        ?? Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture)
                }
            ],
            CreatedAt = now,
            UpdatedAt = now
        };

        context.Anime.Add(anime);
        await context.SaveChangesAsync();

        context.LibraryEntries.Add(new LibraryEntry
        {
            ProfileId = Profile.DefaultProfileId,
            AnimeId = anime.Id,
            Status = status,
            UserScore = userScore,
            RecommendationScore = recommendation,
            RecommendationConfidence = confidence,
            DateAdded = now,
            LastUpdated = now
        });

        await context.SaveChangesAsync();
        return anime;
    }

    [Fact]
    public async Task The_backlog_defaults_to_planning()
    {
        // The backlog is what the user intends to watch. Watching has its own page,
        // and a library is mostly Completed — listing everything buries the handful
        // of entries that are actually a decision.
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Planned", LibraryStatus.Planning);
            await AddAsync(context, "Finished", LibraryStatus.Completed);
            await AddAsync(context, "In progress", LibraryStatus.Watching);
        }

        var page = await fixture.Library.GetPageAsync(Profile.DefaultProfileId, new LibraryQuery());

        Assert.Equal("Planned", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Setting_status_to_null_widens_to_the_whole_library()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Planned", LibraryStatus.Planning);
            await AddAsync(context, "Finished", LibraryStatus.Completed);
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId, new LibraryQuery { Status = null });

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Runtime_filtering_excludes_entries_whose_length_is_unknown()
    {
        // An unknown length is not evidence that something fits in an evening.
        // Including it would put an unknown-length series in the "under 2 hours"
        // list, which is the one place the answer must be trustworthy.
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Short film", episodes: 1, duration: 90);
            await AddAsync(context, "Long series", episodes: 26, duration: 24);
            await AddAsync(context, "Unknown length", episodes: 13, duration: null);
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId, new LibraryQuery { MaxRuntimeMinutes = 120 });

        Assert.Equal("Short film", Assert.Single(page.Items).Title);
    }

    [Theory]
    [InlineData(1980, "Eighties")]
    [InlineData(1990, "Nineties")]
    [InlineData(2020, "Twenties")]
    public async Task Decade_filtering_matches_the_ten_year_span(int decade, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Eighties", year: 1988);
            await AddAsync(context, "Nineties", year: 1995);
            await AddAsync(context, "Twenties", year: 2021);
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId, new LibraryQuery { Decade = decade });

        Assert.Equal(expected, Assert.Single(page.Items).Title);
    }

    /// <summary>
    /// The number beside a status is a promise about what choosing it shows.
    /// </summary>
    /// <remarks>
    /// A picker whose counts do not match its own results is worse than one with no
    /// counts at all. The two used to be able to disagree because the count excluded
    /// hidden entries and so did the listing, and each was capable of forgetting;
    /// Phase 18b removed hiding, and this holds the two to each other anyway.
    /// </remarks>
    [Fact]
    public async Task A_status_count_is_what_choosing_that_status_lists()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Planned");
            await AddAsync(context, "Also planned");
            await AddAsync(context, "Finished", LibraryStatus.Completed);
        }

        var facets = await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId);

        Assert.Equal(2, facets.CountByStatus.GetValueOrDefault(LibraryStatus.Planning));
        Assert.Equal(1, facets.CountByStatus.GetValueOrDefault(LibraryStatus.Completed));

        var planning = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId, new LibraryQuery { Status = LibraryStatus.Planning });

        Assert.Equal(facets.CountByStatus[LibraryStatus.Planning], planning.TotalCount);
    }

    [Fact]
    public async Task Unranked_entries_sort_last_rather_than_first()
    {
        // Ascending null ordering would put every unranked entry above the AI's
        // best pick, which inverts the meaning of "best first".
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Unranked", recommendation: null);
            await AddAsync(context, "Middling", recommendation: 5.0);
            await AddAsync(context, "Best", recommendation: 9.5);
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { Sort = LibrarySort.RecommendationDescending });

        Assert.Equal(["Best", "Middling", "Unranked"], page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Unknown_runtimes_sort_last_when_sorting_by_length()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Unknown", episodes: null, duration: null);
            await AddAsync(context, "Short", episodes: 1, duration: 24);
            await AddAsync(context, "Long", episodes: 50, duration: 24);
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId, new LibraryQuery { Sort = LibrarySort.RuntimeAscending });

        Assert.Equal(["Short", "Long", "Unknown"], page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Paging_is_stable_across_pages_when_sort_keys_tie()
    {
        // Every entry here shares a release year, so the sort key alone cannot
        // order them and the title tiebreak has to do the work.
        // Without a tiebreak, entries sharing a sort key come back in whatever
        // order SQLite produces, which can differ per query — so a title can
        // appear on two pages, or none.
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            for (var i = 1; i <= 10; i++)
            {
                await AddAsync(context, $"Tied {i:00}");
            }
        }

        var first = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { Sort = LibrarySort.YearDescending, Skip = 0, Take = 5 });

        var second = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { Sort = LibrarySort.YearDescending, Skip = 5, Take = 5 });

        var all = first.Items.Concat(second.Items).Select(i => i.Title).ToList();

        Assert.Equal(10, all.Distinct().Count());
    }

    [Fact]
    public async Task Facets_report_only_what_the_library_contains()
    {
        // A filter that cannot match anything is worse than absent: an empty
        // result reads as "I own none of these" rather than "this control is
        // useless here".
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "A movie", mediaType: MediaType.Movie, year: 1995);
            await AddAsync(context, "A series", mediaType: MediaType.Tv, year: 2021);
        }

        var facets = await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId);

        Assert.Equal([MediaType.Tv, MediaType.Movie], facets.MediaTypes.OrderBy(m => m));
        Assert.Equal([1990, 2020], facets.Decades);
        Assert.DoesNotContain(MediaType.Ova, facets.MediaTypes);
    }

    [Fact]
    public async Task Facets_report_absent_metadata_as_absent()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "No runtime", episodes: null, duration: null,
                year: null, userScore: null, recommendation: null);
        }

        var facets = await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId);

        Assert.False(facets.HasRuntimeData);
        Assert.False(facets.HasRecommendations);
        Assert.False(facets.HasUserScores);
        Assert.Empty(facets.Decades);
        Assert.True(facets.HasUnrankedEntries);
    }

    [Fact]
    public async Task An_empty_library_has_no_facets_at_all()
    {
        await using var fixture = await Fixture.CreateAsync();

        var facets = await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId);

        Assert.Empty(facets.MediaTypes);
        Assert.Empty(facets.Decades);
        Assert.False(facets.HasRuntimeData);

        // The one thing the page cannot tell from an empty result: the backlog
        // defaults to Planning, so a fresh install always has a filter applied and
        // would otherwise offer to clear one that is not the reason (D27).
        Assert.True(facets.IsEmpty);
    }

    [Fact]
    public async Task A_library_with_entries_is_not_empty()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Planned");
        }

        Assert.False((await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId)).IsEmpty);
    }

    /// <summary>
    /// The surviving half of the brief's franchise/standalone pair, redefined by
    /// D24 as "no prequel and no sequel edge at all".
    /// </summary>
    /// <remarks>
    /// The edge is stored exactly as the source stated it, so a title with a sequel
    /// may be named at <i>either</i> end of the row that says so — which is why the
    /// filter looks at both columns, and why both directions are asserted here
    /// rather than one being assumed to imply the other.
    /// </remarks>
    [Fact]
    public async Task Standalone_excludes_a_title_at_either_end_of_a_sequel_edge()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Season one", source: AnimeSource.AniList, sourceId: "100");
            await AddAsync(context, "Season two", source: AnimeSource.AniList, sourceId: "200");
            await AddAsync(context, "A film", source: AnimeSource.AniList, sourceId: "300");

            // Written once, from season one's perspective, exactly as the backfill
            // stores it. Both titles are disqualified by it.
            await RelateAsync(context, "100", RelationType.Sequel, "200");

            // A recap is not a commitment: only PREQUEL and SEQUEL disqualify.
            await RelateAsync(context, "300", RelationType.Summary, "400");
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { StandaloneOnly = true });

        Assert.Equal("A film", Assert.Single(page.Items).Title);
    }

    /// <summary>
    /// Counted over every edge rather than only owned ones. A series whose later
    /// seasons the user does not own is still a series, and answering a question
    /// about the show from what the library happens to contain would call it
    /// standalone until season two was imported.
    /// </summary>
    [Fact]
    public async Task Standalone_counts_sequels_the_library_does_not_own()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Season one", source: AnimeSource.AniList, sourceId: "100");
            await RelateAsync(context, "100", RelationType.Sequel, "999");
        }

        var page = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { StandaloneOnly = true });

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task The_standalone_filter_is_offered_only_once_the_graph_can_answer_it()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Season one", source: AnimeSource.AniList, sourceId: "100");
        }

        // Nothing has been fetched yet, so the filter would match every row — a
        // control that appears to work and changes nothing, which reads as "my whole
        // library is standalone".
        Assert.False((await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId)).HasSequelEdges);

        await using (var context = fixture.Database.CreateContext())
        {
            await RelateAsync(context, "100", RelationType.Sequel, "200");
        }

        Assert.True((await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId)).HasSequelEdges);
    }

    /// <summary>
    /// A side story is not a continuation, so a graph made only of those leaves the
    /// standalone filter with nothing to exclude and the chip unoffered.
    /// </summary>
    [Fact]
    public async Task Relations_that_are_not_continuations_do_not_offer_the_filter()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "A series", source: AnimeSource.AniList, sourceId: "100");
            await RelateAsync(context, "100", RelationType.SideStory, "200");
        }

        Assert.False((await fixture.Library.GetFacetsAsync(Profile.DefaultProfileId)).HasSequelEdges);
    }

    private static async Task RelateAsync(
        AniQueueDbContext context,
        string externalId,
        RelationType type,
        string relatedExternalId)
    {
        context.AnimeRelations.Add(new AnimeRelation
        {
            Source = AnimeSource.AniList,
            ExternalId = externalId,
            RelationType = type,
            RelatedExternalId = relatedExternalId
        });

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Rows_report_whether_they_are_already_queued()
    {
        await using var fixture = await Fixture.CreateAsync();
        int queuedId, looseId;

        await using (var context = fixture.Database.CreateContext())
        {
            queuedId = (await AddAsync(context, "Queued")).Id;
            looseId = (await AddAsync(context, "Not queued")).Id;
        }

        await fixture.Queue.AddAnimeAsync(Profile.DefaultProfileId, [queuedId]);

        var page = await fixture.Library.GetPageAsync(Profile.DefaultProfileId, new LibraryQuery());

        Assert.True(page.Items.Single(i => i.AnimeId == queuedId).IsQueued);
        Assert.False(page.Items.Single(i => i.AnimeId == looseId).IsQueued);
    }

    [Fact]
    public async Task Rows_carry_a_link_back_to_their_source()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Golden Boy", source: AnimeSource.MyAnimeList, sourceId: "268");
        }

        var page = await fixture.Library.GetPageAsync(Profile.DefaultProfileId, new LibraryQuery());
        var link = Assert.Single(Assert.Single(page.Items).SourceLinks);

        Assert.Equal("https://myanimelist.net/anime/268", link.Url);
    }
}

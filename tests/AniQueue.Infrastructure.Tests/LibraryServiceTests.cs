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
        string? sourceId = null,
        bool hidden = false,
        int priority = 0,
        int? franchiseId = null)
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
            SourceAnimeId = sourceId ?? Guid.NewGuid().ToString("N")[..8],
            FranchiseId = franchiseId,
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
            IsHidden = hidden,
            ManualPriority = priority,
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

    [Fact]
    public async Task Hidden_entries_stay_out_of_listings_unless_asked_for()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            await AddAsync(context, "Visible");
            await AddAsync(context, "Hidden", hidden: true);
        }

        var normal = await fixture.Library.GetPageAsync(Profile.DefaultProfileId, new LibraryQuery());
        Assert.Equal("Visible", Assert.Single(normal.Items).Title);

        var withHidden = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId, new LibraryQuery { IncludeHidden = true });
        Assert.Equal(2, withHidden.TotalCount);
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
        // Without a tiebreak, entries sharing a sort key come back in whatever
        // order SQLite produces, which can differ per query — so a title can
        // appear on two pages, or none.
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            for (var i = 1; i <= 10; i++)
            {
                await AddAsync(context, $"Tied {i:00}", priority: 5);
            }
        }

        var first = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { Sort = LibrarySort.PriorityDescending, Skip = 0, Take = 5 });

        var second = await fixture.Library.GetPageAsync(
            Profile.DefaultProfileId,
            new LibraryQuery { Sort = LibrarySort.PriorityDescending, Skip = 5, Take = 5 });

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
        Assert.False(facets.HasFranchises);
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
    }

    [Fact]
    public async Task Bulk_priority_applies_to_the_selection_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        int firstId, secondId, untouchedId;

        await using (var context = fixture.Database.CreateContext())
        {
            firstId = (await AddAsync(context, "First")).Id;
            secondId = (await AddAsync(context, "Second")).Id;
            untouchedId = (await AddAsync(context, "Untouched")).Id;
        }

        var result = await fixture.Library.SetPriorityAsync(
            Profile.DefaultProfileId, [firstId, secondId], priority: 7);

        Assert.Equal(2, result.Affected);

        await using var verify = fixture.Database.CreateContext();
        Assert.Equal(7, (await verify.LibraryEntries.SingleAsync(e => e.AnimeId == firstId)).ManualPriority);
        Assert.Equal(7, (await verify.LibraryEntries.SingleAsync(e => e.AnimeId == secondId)).ManualPriority);
        Assert.Equal(0, (await verify.LibraryEntries.SingleAsync(e => e.AnimeId == untouchedId)).ManualPriority);
    }

    [Fact]
    public async Task Hiding_is_reversible_and_keeps_the_entry()
    {
        // Hiding must never be a disguised delete: the entry, its score and its
        // history all survive.
        await using var fixture = await Fixture.CreateAsync();
        int id;

        await using (var context = fixture.Database.CreateContext())
        {
            id = (await AddAsync(context, "Hide me", userScore: 8)).Id;
        }

        await fixture.Library.SetHiddenAsync(Profile.DefaultProfileId, [id], hidden: true);
        await fixture.Library.SetHiddenAsync(Profile.DefaultProfileId, [id], hidden: false);

        await using var verify = fixture.Database.CreateContext();
        var entry = await verify.LibraryEntries.SingleAsync(e => e.AnimeId == id);

        Assert.False(entry.IsHidden);
        Assert.Equal(8, entry.UserScore);
    }

    [Fact]
    public async Task A_bulk_action_on_nothing_does_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Library.SetPriorityAsync(Profile.DefaultProfileId, [], priority: 5);

        Assert.Equal(0, result.Affected);
    }

    [Fact]
    public async Task Ids_that_are_not_in_the_library_are_counted_as_skipped()
    {
        // A stale selection must not fail the whole batch.
        await using var fixture = await Fixture.CreateAsync();
        int realId;

        await using (var context = fixture.Database.CreateContext())
        {
            realId = (await AddAsync(context, "Real")).Id;
        }

        var result = await fixture.Library.SetPriorityAsync(
            Profile.DefaultProfileId, [realId, 999_999], priority: 3);

        Assert.Equal(1, result.Affected);
        Assert.Equal(1, result.Skipped);
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
        var link = Assert.Single(page.Items).SourceLink;

        Assert.NotNull(link);
        Assert.Equal("https://myanimelist.net/anime/268", link.Url);
    }
}

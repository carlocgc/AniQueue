using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Library;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// What an expansion shows, against a real graph in a real database.
///
/// Two properties carry most of these. Edges are stored exactly as the source
/// stated them (D24), so a title's relatives are found by searching <i>both</i>
/// columns and inverting what came back from the far end — and the count on the
/// chevron has to mean exactly what the panel below it opens, or a badge promising
/// three relatives opens onto one.
/// </summary>
public class RelationServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

        public required IRelationService Relations { get; init; }

        public required int ProfileId { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var database = await SqliteTestDatabase.CreateAsync();

            await new DatabaseInitializer(
                database.ContextFactory,
                Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
                NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

            await using var context = database.CreateContext();
            var profile = await SeedData.CreateProfileAsync(context);

            return new Fixture
            {
                Database = database,
                Relations = new RelationService(database.ContextFactory),
                ProfileId = profile.Id
            };
        }

        /// <summary>
        /// A title the profile owns, carrying the AniList identifier the graph
        /// speaks in.
        /// </summary>
        public async Task<int> OwnAsync(
            string externalId,
            string? title = null,
            LibraryStatus status = LibraryStatus.Planning,
            bool hidden = false,
            DateOnly? startDate = null,
            int? year = null)
        {
            await using var context = Database.CreateContext();

            var now = DateTimeOffset.UtcNow;

            var anime = new Anime
            {
                Title = title ?? $"Title {externalId}",
                Source = AnimeSource.AniList,
                StartDate = startDate,
                ReleaseYear = year,
                ExternalIds = [new AnimeExternalId { Source = AnimeSource.AniList, ExternalId = externalId }],
                CreatedAt = now,
                UpdatedAt = now
            };

            context.Anime.Add(anime);
            await context.SaveChangesAsync();

            context.LibraryEntries.Add(new LibraryEntry
            {
                ProfileId = ProfileId,
                AnimeId = anime.Id,
                Status = status,
                IsHidden = hidden,
                DateAdded = now,
                LastUpdated = now
            });

            await context.SaveChangesAsync();

            return anime.Id;
        }

        /// <summary>
        /// One edge, written the way the backfill writes it: from the perspective
        /// of the title that was asked about.
        /// </summary>
        public async Task RelateAsync(string externalId, RelationType type, string relatedExternalId)
        {
            await using var context = Database.CreateContext();

            context.AnimeRelations.Add(new AnimeRelation
            {
                Source = AnimeSource.AniList,
                ExternalId = externalId,
                RelationType = type,
                RelatedExternalId = relatedExternalId
            });

            await context.SaveChangesAsync();
        }

        public async Task QueueAsync(int animeId)
        {
            await using var context = Database.CreateContext();
            context.QueueItems.Add(SeedData.QueueSlot(ProfileId, 1, animeId));
            await context.SaveChangesAsync();
        }

        public Task<IReadOnlyList<RelatedTitle>> RelatedAsync(int animeId) =>
            Relations.GetRelatedAsync(ProfileId, animeId);

        public Task<IReadOnlyDictionary<int, int>> CountsAsync(params int[] animeIds) =>
            Relations.GetRelatedCountsAsync(ProfileId, animeIds);

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    [Fact]
    public async Task A_title_the_source_named_is_shown_with_the_type_it_named()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100");
        var second = await fixture.OwnAsync("200");
        await fixture.RelateAsync("100", RelationType.Sequel, "200");

        var related = Assert.Single(await fixture.RelatedAsync(first));

        Assert.Equal(second, related.AnimeId);
        Assert.Equal(RelationType.Sequel, related.Relation);
        Assert.Equal("Sequel", related.Label);
    }

    /// <summary>
    /// The reverse index earning its place. Half a graph is only ever reachable
    /// this way: a title whose own relations have never been fetched appears
    /// nowhere as an <c>ExternalId</c>, only as somebody else's related end.
    /// </summary>
    [Fact]
    public async Task A_relation_stated_by_the_far_end_is_inverted()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100");
        var second = await fixture.OwnAsync("200");

        // "200 has prequel 100" — the only row, written when 200 was asked about.
        await fixture.RelateAsync("200", RelationType.Prequel, "100");

        var fromFirst = Assert.Single(await fixture.RelatedAsync(first));
        Assert.Equal(second, fromFirst.AnimeId);
        Assert.Equal(RelationType.Sequel, fromFirst.Relation);

        var fromSecond = Assert.Single(await fixture.RelatedAsync(second));
        Assert.Equal(first, fromSecond.AnimeId);
        Assert.Equal(RelationType.Prequel, fromSecond.Relation);
    }

    /// <summary>
    /// The same fact stated from both ends is one relative, not two. AniList
    /// publishes both halves, and the backfill stores both, so a count of rows
    /// would put a 2 on a chevron that opens onto one title.
    /// </summary>
    [Fact]
    public async Task The_same_pair_stated_twice_counts_once()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200");

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Prequel, "100");

        Assert.Single(await fixture.RelatedAsync(first));

        var counts = await fixture.CountsAsync(first);
        Assert.Equal(1, counts[first]);
    }

    /// <summary>
    /// AniList uses <c>PARENT</c> as the counterpart of both <c>SIDE_STORY</c> and
    /// <c>SPIN_OFF</c>, so the two ends of one connection routinely describe it
    /// differently. Choosing a winner would state something the source did not.
    /// </summary>
    [Fact]
    public async Task Edges_that_disagree_about_the_connection_are_labelled_related()
    {
        await using var fixture = await Fixture.CreateAsync();

        var main = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200");

        await fixture.RelateAsync("100", RelationType.SpinOff, "200");
        await fixture.RelateAsync("200", RelationType.Parent, "100");

        var related = Assert.Single(await fixture.RelatedAsync(main));

        Assert.Null(related.Relation);
        Assert.Equal("Related", related.Label);
    }

    [Fact]
    public async Task A_relative_the_library_does_not_own_is_neither_counted_nor_shown()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");

        // 999 is a real title on AniList and absent here, which is the ordinary
        // case: the graph reaches thousands of titles nobody has expressed an
        // interest in (D11).
        await fixture.RelateAsync("100", RelationType.Sequel, "999");

        Assert.Empty(await fixture.RelatedAsync(owned));
        Assert.Empty(await fixture.CountsAsync(owned));
    }

    [Fact]
    public async Task A_hidden_relative_is_neither_counted_nor_shown()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200", hidden: true);
        await fixture.RelateAsync("100", RelationType.Sequel, "200");

        Assert.Empty(await fixture.RelatedAsync(owned));
        Assert.Empty(await fixture.CountsAsync(owned));
    }

    /// <summary>
    /// An expansion is context rather than results. A completed prequel is
    /// frequently the most useful thing it can say, so it is not filtered the way
    /// the listing above it is.
    /// </summary>
    [Theory]
    [InlineData(LibraryStatus.Completed)]
    [InlineData(LibraryStatus.Watching)]
    [InlineData(LibraryStatus.Dropped)]
    [InlineData(LibraryStatus.OnHold)]
    public async Task Every_status_except_hidden_is_shown(LibraryStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200", status: status);
        await fixture.RelateAsync("100", RelationType.Prequel, "200");

        var related = Assert.Single(await fixture.RelatedAsync(owned));
        Assert.Equal(status, related.Status);
    }

    /// <summary>
    /// One edge out, never transitive. Season five is not the sequel of season one,
    /// and a walk that kept going would pull a whole franchise into a panel opened
    /// to answer a much smaller question.
    /// </summary>
    [Fact]
    public async Task Nothing_is_transitive_beyond_one_edge()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100");
        var second = await fixture.OwnAsync("200");
        await fixture.OwnAsync("300");

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        var related = Assert.Single(await fixture.RelatedAsync(first));
        Assert.Equal(second, related.AnimeId);

        var counts = await fixture.CountsAsync(first);
        Assert.Equal(1, counts[first]);
    }

    /// <summary>
    /// Release order, and a date finer than the year: two halves of a split-cour
    /// series share one, which is exactly the case this ordering exists to get
    /// right (D24).
    /// </summary>
    [Fact]
    public async Task Relatives_are_ordered_by_release_date_with_unknown_dates_last()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");

        var autumn = await fixture.OwnAsync("300", "Second cour", startDate: new DateOnly(2015, 10, 3), year: 2015);
        var spring = await fixture.OwnAsync("200", "First cour", startDate: new DateOnly(2015, 4, 5), year: 2015);
        var undated = await fixture.OwnAsync("400", "Announced only");

        foreach (var id in new[] { "200", "300", "400" })
        {
            await fixture.RelateAsync("100", RelationType.SideStory, id);
        }

        var related = await fixture.RelatedAsync(owned);

        Assert.Equal([spring, autumn, undated], related.Select(r => r.AnimeId));
    }

    [Fact]
    public async Task A_queued_relative_says_so()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");
        var relative = await fixture.OwnAsync("200");
        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.QueueAsync(relative);

        var related = Assert.Single(await fixture.RelatedAsync(owned));
        Assert.True(related.IsQueued);
    }

    /// <summary>
    /// A title is not its own relative. Reachable in practice through a
    /// self-referential edge, and the result would be a row that expands to itself.
    /// </summary>
    [Fact]
    public async Task A_title_is_never_related_to_itself()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");
        await fixture.RelateAsync("100", RelationType.Alternative, "100");

        Assert.Empty(await fixture.RelatedAsync(owned));
        Assert.Empty(await fixture.CountsAsync(owned));
    }

    /// <summary>
    /// Absent rather than zero, because the page reads absence as "draw no
    /// chevron". A control that sometimes does nothing teaches people to stop
    /// pressing it.
    /// </summary>
    [Fact]
    public async Task Titles_with_no_relatives_are_absent_from_the_counts()
    {
        await using var fixture = await Fixture.CreateAsync();

        var withRelative = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200");
        var standalone = await fixture.OwnAsync("300");

        await fixture.RelateAsync("100", RelationType.Sequel, "200");

        var counts = await fixture.CountsAsync(withRelative, standalone);

        Assert.Equal(1, counts[withRelative]);
        Assert.False(counts.ContainsKey(standalone));
    }

    [Fact]
    public async Task Counting_nothing_asks_the_database_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Empty(await fixture.CountsAsync());
    }

    /// <summary>
    /// The count and the expansion share one definition of a relative, which is the
    /// only thing stopping a badge promising more than the panel opens.
    /// </summary>
    [Fact]
    public async Task The_count_matches_what_the_expansion_lists()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200", status: LibraryStatus.Completed);
        await fixture.OwnAsync("300", hidden: true);
        await fixture.OwnAsync("400");

        await fixture.RelateAsync("100", RelationType.Prequel, "200");
        await fixture.RelateAsync("300", RelationType.Sequel, "100");
        await fixture.RelateAsync("400", RelationType.SpinOff, "100");
        await fixture.RelateAsync("100", RelationType.Sequel, "999");

        var counts = await fixture.CountsAsync(owned);
        var related = await fixture.RelatedAsync(owned);

        Assert.Equal(related.Count, counts[owned]);
        Assert.Equal(2, related.Count);
    }
}

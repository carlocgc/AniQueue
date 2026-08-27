using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Core.Queue;
using AniQueue.Infrastructure.Library;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Queue;
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

        /// <summary>
        /// The real queue service, not a stub. The set walk hands its ordered set
        /// to <c>AddAnimeAsync</c> precisely so that queue eligibility and the
        /// contiguity invariant stay in one place — replacing it here would leave
        /// that hand-off untested at the only seam where it can go wrong.
        /// </summary>
        public required IQueueService Queue { get; init; }

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

            var queue = new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance);

            return new Fixture
            {
                Database = database,
                Relations = new RelationService(database.ContextFactory, queue),
                Queue = queue,
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

        /// <summary>The titles in the set, as one string, in the order they come back.</summary>
        public async Task<string> SetOrderAsync(int animeId) =>
            string.Join(" ", (await RelatedAsync(animeId)).Select(r => r.Title));

        /// <summary>The queue's titles in order, as one string for readable assertions.</summary>
        public async Task<string> QueueOrderAsync()
        {
            var slots = await Queue.GetQueueAsync(ProfileId);
            return string.Join(" ", slots.Select(s => s.Title));
        }

        /// <summary>
        /// Reads positions straight from the table rather than through the ordered
        /// read, which would hide exactly the corruption being checked for (D2).
        /// </summary>
        public async Task AssertQueueContiguousAsync()
        {
            await using var context = Database.CreateContext();

            var positions = await context.QueueItems
                .AsNoTracking()
                .Where(q => q.ProfileId == ProfileId)
                .Select(q => q.Position)
                .ToListAsync();

            Assert.Equal(Enumerable.Range(0, positions.Count), positions.Order());
        }

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
    /// The same fact stated from both ends is one title in the set, not two.
    /// AniList publishes both halves and the backfill stores both.
    /// </summary>
    [Fact]
    public async Task The_same_pair_stated_twice_appears_once()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200");

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Prequel, "100");

        Assert.Single(await fixture.RelatedAsync(first));
    }

    /// <summary>
    /// AniList uses <c>PARENT</c> as the counterpart of both <c>SIDE_STORY</c> and
    /// <c>SPIN_OFF</c>, so the two ends of one connection routinely describe it
    /// differently. Choosing a winner would state something the source did not.
    /// </summary>
    /// <remarks>
    /// Both ends here call the other their side story, which inverts to "parent" read
    /// from the far end — so the two descriptions disagree while the pair is still
    /// plainly the same work. The spin-off version of this disagreement is no longer
    /// expressible in a set at all: D55 stopped following parent edges, so a pair
    /// joined only by <c>SPIN_OFF</c> and <c>PARENT</c> is not in one.
    /// </remarks>
    [Fact]
    public async Task Edges_that_disagree_about_the_connection_are_labelled_related()
    {
        await using var fixture = await Fixture.CreateAsync();

        var main = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200");

        await fixture.RelateAsync("100", RelationType.SideStory, "200");
        await fixture.RelateAsync("200", RelationType.SideStory, "100");

        var related = Assert.Single(await fixture.RelatedAsync(main));

        Assert.Null(related.Relation);
        Assert.Equal("Related", related.Label);
    }

    [Fact]
    public async Task A_relative_the_library_does_not_own_is_not_in_the_set()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");

        // 999 is a real title on AniList and absent here, which is the ordinary
        // case: the graph reaches thousands of titles nobody has expressed an
        // interest in (D11).
        await fixture.RelateAsync("100", RelationType.Sequel, "999");

        Assert.Empty(await fixture.RelatedAsync(owned));
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
    public async Task Every_status_is_shown(LibraryStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100");
        await fixture.OwnAsync("200", status: status);
        await fixture.RelateAsync("100", RelationType.Prequel, "200");

        var related = Assert.Single(await fixture.RelatedAsync(owned));
        Assert.Equal(status, related.Status);
    }

    /// <summary>
    /// The set follows the same work as far as it goes, so season three is in season
    /// one's set with a season between them (D55). This is the rule D24 wrote the
    /// other way round, and the surface changed underneath it: one edge out was right
    /// for a panel wedged into a table row, and wrong for a dialog answering "what am
    /// I taking on if I start here".
    /// </summary>
    [Fact]
    public async Task A_season_two_edges_away_is_still_in_the_set()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));
        await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        Assert.Equal("Season two Season three", await fixture.SetOrderAsync(first));
    }

    /// <summary>
    /// Only the neighbour carries a label. Nothing states how season one relates to
    /// season three, so naming it would be inventing a relationship the source did
    /// not publish (D55).
    /// </summary>
    [Fact]
    public async Task Only_a_title_one_edge_away_is_labelled()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));
        await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        var set = await fixture.RelatedAsync(first);

        Assert.Equal(RelationType.Sequel, set.Single(r => r.Title == "Season two").Relation);
        Assert.Null(set.Single(r => r.Title == "Season three").Relation);
    }

    /// <summary>
    /// What a box set does not hold. A spin-off is a separate work set in the same
    /// world and an alternative is the same story told again — neither is a season of
    /// the show you opened, and neither is walked through to reach anything else
    /// (D55).
    /// </summary>
    [Theory]
    [InlineData(RelationType.SpinOff)]
    [InlineData(RelationType.Alternative)]
    public async Task A_spin_off_or_a_remake_is_not_in_the_set(RelationType type)
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100", "The show");
        await fixture.OwnAsync("200", "Something else");
        await fixture.OwnAsync("300", "Its sequel");

        await fixture.RelateAsync("100", type, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        // Not the far title either: excluding the edge has to stop the walk rather
        // than merely hide one row of it.
        Assert.Empty(await fixture.RelatedAsync(owned));
    }

    /// <summary>
    /// A special hangs off the work it belongs to, and the edge is traversable from
    /// either end — so it is in the set read from the special as well as from the
    /// show (D55).
    /// </summary>
    [Fact]
    public async Task A_special_is_in_the_set_from_either_end()
    {
        await using var fixture = await Fixture.CreateAsync();

        var show = await fixture.OwnAsync("100", "The show");
        var ova = await fixture.OwnAsync("200", "The OVA");

        await fixture.RelateAsync("100", RelationType.SideStory, "200");

        Assert.Equal("The OVA", await fixture.SetOrderAsync(show));
        Assert.Equal("The show", await fixture.SetOrderAsync(ova));
    }

    /// <summary>
    /// A spin-off does not drag in the work it branches from, and this is the case
    /// that made the rule: <c>PARENT</c> is AniList's counterpart to both
    /// <c>SIDE_STORY</c> and <c>SPIN_OFF</c> (D24), so following it cannot tell a
    /// special from a spin-off.
    /// </summary>
    /// <remarks>
    /// Measured on a real library before it was written. Prisma Illya states
    /// <c>PARENT</c> to Unlimited Blade Works, which states <c>SPIN_OFF</c> back, and
    /// following the parent edge put Fate/Zero — two further edges away, through a
    /// series Illya is not part of — into Illya's box set.
    /// </remarks>
    [Fact]
    public async Task A_spin_off_does_not_reach_the_work_it_branches_from()
    {
        await using var fixture = await Fixture.CreateAsync();

        var spinOff = await fixture.OwnAsync("300", "The spin-off");
        await fixture.OwnAsync("100", "The main work");
        await fixture.OwnAsync("050", "Its prequel");

        // Both halves, as AniList publishes them: the spin-off calls the main work its
        // parent, and the main work calls the spin-off a spin-off.
        await fixture.RelateAsync("300", RelationType.Parent, "100");
        await fixture.RelateAsync("100", RelationType.SpinOff, "300");
        await fixture.RelateAsync("050", RelationType.Sequel, "100");

        Assert.Empty(await fixture.RelatedAsync(spinOff));
    }

    /// <summary>
    /// The cost of the rule above, stated rather than discovered: a special whose only
    /// stored edge is its own <c>PARENT</c> is not in the set, because that row is
    /// exactly the one that cannot be told from a spin-off (D55).
    /// </summary>
    [Fact]
    public async Task A_special_that_only_names_its_parent_is_left_out()
    {
        await using var fixture = await Fixture.CreateAsync();

        var show = await fixture.OwnAsync("100", "The show");
        await fixture.OwnAsync("200", "The OVA");

        await fixture.RelateAsync("200", RelationType.Parent, "100");

        Assert.Empty(await fixture.RelatedAsync(show));
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
    /// A title is not in its own set. It is in the walk — QueueSetAsync needs it
    /// there — and it is removed before the list is handed over, or the dialog would
    /// list the title it is open on.
    /// </summary>
    [Fact]
    public async Task A_title_is_never_in_its_own_set()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100", "Only");
        await fixture.RelateAsync("100", RelationType.Sequel, "100");

        Assert.Empty(await fixture.RelatedAsync(owned));
    }

    /// <summary>
    /// The one place a spin-off and a set meet: the spin-off is excluded, everything
    /// the same work reaches is not, and an unowned title is a gap rather than a row.
    /// </summary>
    [Fact]
    public async Task The_set_holds_the_same_work_and_nothing_else()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owned = await fixture.OwnAsync("100", "The show");
        await fixture.OwnAsync("200", "Its prequel", status: LibraryStatus.Completed);
        await fixture.OwnAsync("300", "Its sequel");
        await fixture.OwnAsync("400", "A spin-off");

        await fixture.RelateAsync("100", RelationType.Prequel, "200");
        await fixture.RelateAsync("300", RelationType.Sequel, "100");
        await fixture.RelateAsync("400", RelationType.SpinOff, "100");
        await fixture.RelateAsync("100", RelationType.Sequel, "999");

        var set = await fixture.RelatedAsync(owned);

        Assert.Equal(["Its prequel", "Its sequel"], set.Select(r => r.Title).Order());
    }

    // --- Queueing a set ---------------------------------------------------
    //
    // The same walk the list above is built from, handed to the queue instead of to
    // the dialog. What differs is only what happens to each title once it is found:
    // the list shows every status, and the queue takes the ones still planned (D55).

    [Fact]
    public async Task The_set_queues_a_title_and_everything_that_follows_it()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));
        await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal(3, result.Added);
        Assert.Equal("Season one Season two Season three", await fixture.QueueOrderAsync());
        await fixture.AssertQueueContiguousAsync();
    }

    /// <summary>
    /// It goes backwards now, and status is what stops the walk proposing what has
    /// already been watched (D55). The old rule was "forward only", which was the
    /// same protection spelled as a direction — and it also hid the unwatched prequel
    /// that is the best reason not to start here.
    /// </summary>
    [Fact]
    public async Task An_unwatched_prequel_is_queued_and_a_watched_one_is_not()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.OwnAsync("100", "Season one", status: LibraryStatus.Completed,
            startDate: new DateOnly(2015, 1, 8));
        var two = await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));
        await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));
        await fixture.OwnAsync("050", "The pilot", startDate: new DateOnly(2014, 7, 2));

        await fixture.RelateAsync("050", RelationType.Sequel, "100");
        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, two);

        // The pilot leads because it aired first; season one is reached and refused.
        Assert.Equal("The pilot Season two Season three", await fixture.QueueOrderAsync());
        Assert.Equal(1, result.NoLongerPlanned);
    }

    /// <summary>
    /// A season the user does not own has edges but no library row, and it must not
    /// end the chain — bridging that gap is a large part of why the walk resolves to
    /// library rows only at the end.
    /// </summary>
    [Fact]
    public async Task The_set_passes_through_a_season_the_library_does_not_own()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));

        // 200 exists on AniList and not here.
        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal(2, result.Added);
        Assert.Equal("Season one Season three", await fixture.QueueOrderAsync());
    }

    /// <summary>
    /// A Completed season four between three and five must not stop the walk, and
    /// must be accounted for rather than silently dropped.
    /// </summary>
    [Fact]
    public async Task The_set_traverses_through_a_watched_season_and_counts_it_as_skipped()
    {
        await using var fixture = await Fixture.CreateAsync();

        var three = await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));
        await fixture.OwnAsync("400", "Season four", LibraryStatus.Completed, startDate: new DateOnly(2018, 4, 2));
        await fixture.OwnAsync("500", "Season five", startDate: new DateOnly(2019, 1, 7));

        await fixture.RelateAsync("300", RelationType.Sequel, "400");
        await fixture.RelateAsync("400", RelationType.Sequel, "500");

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, three);

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.NoLongerPlanned);
        Assert.Equal("Season three Season five", await fixture.QueueOrderAsync());
    }

    /// <summary>
    /// Release order, not walk order. AniList publishes no viewing sequence, and the
    /// edges alone would give story order — frequently the wrong watch order (D24).
    /// </summary>
    [Fact]
    public async Task The_set_appends_in_release_order_rather_than_the_order_it_found_them()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "First", startDate: new DateOnly(2015, 1, 8));

        // Two sequels of the same season, discovered in one step, so nothing about
        // the traversal decides which comes first.
        await fixture.OwnAsync("300", "Later", startDate: new DateOnly(2017, 10, 3));
        await fixture.OwnAsync("200", "Sooner", startDate: new DateOnly(2016, 4, 5));

        await fixture.RelateAsync("100", RelationType.Sequel, "300");
        await fixture.RelateAsync("100", RelationType.Sequel, "200");

        await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal("First Sooner Later", await fixture.QueueOrderAsync());
    }

    /// <summary>
    /// A recap film sits in the middle of a chain rather than off it: AniList
    /// routinely publishes one as the sequel of the season before and the prequel of
    /// the season after, so a SEQUEL-only walk reaches it.
    /// </summary>
    [Fact]
    public async Task A_recap_in_the_middle_of_the_chain_is_passed_through_but_not_queued()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("150", "Recap film", startDate: new DateOnly(2016, 1, 9));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));

        await fixture.RelateAsync("100", RelationType.Sequel, "150");
        await fixture.RelateAsync("150", RelationType.Sequel, "200");

        // What makes it a recap, stated the way the source states it.
        await fixture.RelateAsync("100", RelationType.Summary, "150");

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal(2, result.Added);
        Assert.Equal("Season one Season two", await fixture.QueueOrderAsync());
    }

    [Fact]
    public async Task A_compilation_in_the_chain_is_passed_through_but_not_queued()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("150", "Compilation film", startDate: new DateOnly(2016, 1, 9));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));

        await fixture.RelateAsync("100", RelationType.Sequel, "150");
        await fixture.RelateAsync("150", RelationType.Sequel, "200");

        // Stated from the compilation's own side this time — "150 contains 100" —
        // which is the other of the two forms that identify one.
        await fixture.RelateAsync("150", RelationType.Contains, "100");

        await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal("Season one Season two", await fixture.QueueOrderAsync());
    }

    /// <summary>
    /// Relation data is maintained by people, and a graph saying two titles follow
    /// each other is a mistake the walk has to survive rather than spin on.
    /// </summary>
    [Fact]
    public async Task A_cycle_in_the_graph_terminates()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "A", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("200", "B", startDate: new DateOnly(2016, 4, 5));

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "100");

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal(2, result.Added);
        Assert.Equal("A B", await fixture.QueueOrderAsync());
    }

    [Fact]
    public async Task Running_the_walk_twice_adds_nothing_the_second_time()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));
        await fixture.RelateAsync("100", RelationType.Sequel, "200");

        await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);
        var again = await fixture.Relations.QueueSetAsync(fixture.ProfileId, one);

        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.AlreadyQueued);
        Assert.Equal("Season one Season two", await fixture.QueueOrderAsync());
        await fixture.AssertQueueContiguousAsync();
    }

    /// <summary>
    /// The count is what the press will actually append, so the action can name its
    /// own size without over-promising.
    /// </summary>
    [Fact]
    public async Task The_count_reports_only_what_would_actually_be_queued()
    {
        await using var fixture = await Fixture.CreateAsync();

        var one = await fixture.OwnAsync("100", "Season one", startDate: new DateOnly(2015, 1, 8));
        await fixture.OwnAsync("200", "Season two", startDate: new DateOnly(2016, 4, 5));
        var three = await fixture.OwnAsync("300", "Season three", startDate: new DateOnly(2017, 10, 3));
        await fixture.OwnAsync("400", "Season four", LibraryStatus.Completed, startDate: new DateOnly(2018, 4, 2));

        await fixture.RelateAsync("100", RelationType.Sequel, "200");
        await fixture.RelateAsync("200", RelationType.Sequel, "300");
        await fixture.RelateAsync("300", RelationType.Sequel, "400");

        // Four titles in the chain, but season four was watched, so three would go.
        Assert.Equal(3, await fixture.Relations.CountToQueueAsync(fixture.ProfileId, one));

        await fixture.QueueAsync(three);

        // Season three is now spoken for as well, leaving one and two.
        Assert.Equal(2, await fixture.Relations.CountToQueueAsync(fixture.ProfileId, one));
    }

    [Fact]
    public async Task A_title_with_nothing_following_it_reports_only_itself()
    {
        await using var fixture = await Fixture.CreateAsync();

        var only = await fixture.OwnAsync("100", "Standalone");

        Assert.Equal(1, await fixture.Relations.CountToQueueAsync(fixture.ProfileId, only));

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, only);

        Assert.Equal(1, result.Added);
    }

    /// <summary>
    /// A MyAnimeList-only title carries no identifier the graph speaks in, so there
    /// is nothing that can be said to follow it — the action is not offered rather
    /// than offered and queueing one title (D23).
    /// </summary>
    [Fact]
    public async Task A_title_with_no_anilist_identifier_has_no_chain_at_all()
    {
        await using var fixture = await Fixture.CreateAsync();

        int animeId;

        await using (var context = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(
                context, "MyAnimeList only", AnimeSource.MyAnimeList, "555");

            context.LibraryEntries.Add(SeedData.Entry(fixture.ProfileId, anime.Id));
            await context.SaveChangesAsync();

            animeId = anime.Id;
        }

        Assert.Equal(0, await fixture.Relations.CountToQueueAsync(fixture.ProfileId, animeId));

        var result = await fixture.Relations.QueueSetAsync(fixture.ProfileId, animeId);

        Assert.Equal(0, result.Added);
        Assert.Empty(await fixture.QueueOrderAsync());
    }
}

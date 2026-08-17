using AniQueue.Core.Domain;
using AniQueue.Core.Queue;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The reorder tests D2 calls load-bearing.
///
/// Dropping the unique index over (ProfileId, Position) moved contiguity and
/// uniqueness out of the schema and into <see cref="QueueService"/>. Nothing in the
/// database will catch a regression here, so every test that mutates the queue
/// asserts the invariant afterwards rather than only checking the order it asked
/// for.
/// </summary>
public class QueueServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

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

            return new Fixture
            {
                Database = database,
                Queue = new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance),
                ProfileId = profile.Id
            };
        }

        /// <summary>Queues the given titles in order and returns their slot ids.</summary>
        public async Task<int[]> QueueTitlesAsync(params string[] titles)
        {
            await using var context = Database.CreateContext();

            var ids = new List<int>();

            foreach (var title in titles)
            {
                var anime = await SeedData.CreateAnimeAsync(context, title);
                context.LibraryEntries.Add(SeedData.Entry(ProfileId, anime.Id));
                ids.Add(anime.Id);
            }

            await context.SaveChangesAsync();
            await Queue.AddAnimeAsync(ProfileId, ids);

            var slots = await Queue.GetQueueAsync(ProfileId);
            return [.. slots.Select(s => s.QueueItemId)];
        }

        /// <summary>The queue's titles in order, as one string for readable assertions.</summary>
        public async Task<string> OrderAsync()
        {
            var slots = await Queue.GetQueueAsync(ProfileId);
            return string.Join(" ", slots.Select(s => s.Title));
        }

        /// <summary>
        /// Reads positions straight from the table — deliberately not through
        /// <see cref="IQueueService.GetQueueAsync"/>, which orders its results and
        /// would therefore hide exactly the corruption being tested for.
        /// </summary>
        public async Task AssertContiguousAsync()
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

    // --- Ordering --------------------------------------------------------

    [Theory]
    [InlineData(QueueMove.Up, "A C B D")]
    [InlineData(QueueMove.Down, "A B D C")]
    [InlineData(QueueMove.Top, "C A B D")]
    [InlineData(QueueMove.Bottom, "A B D C")]
    public async Task Moving_a_slot_reorders_the_queue_and_leaves_it_contiguous(QueueMove move, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C", "D");

        Assert.True(await fixture.Queue.MoveAsync(fixture.ProfileId, slots[2], move));

        Assert.Equal(expected, await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    [Theory]
    [InlineData(0, QueueMove.Up)]
    [InlineData(0, QueueMove.Top)]
    [InlineData(2, QueueMove.Down)]
    [InlineData(2, QueueMove.Bottom)]
    public async Task A_move_that_changes_nothing_reports_that_it_did_nothing(int index, QueueMove move)
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C");

        Assert.False(await fixture.Queue.MoveAsync(fixture.ProfileId, slots[index], move));

        Assert.Equal("A B C", await fixture.OrderAsync());
    }

    [Fact]
    public async Task Repeated_moves_keep_the_queue_intact()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C", "D", "E", "F");

        // A deterministic walk rather than random input: it has to fail the same
        // way twice to be worth anything as a regression test.
        var moves = new[]
        {
            (Slot: 0, Move: QueueMove.Bottom),
            (Slot: 5, Move: QueueMove.Top),
            (Slot: 3, Move: QueueMove.Up),
            (Slot: 1, Move: QueueMove.Down),
            (Slot: 2, Move: QueueMove.Bottom),
            (Slot: 4, Move: QueueMove.Up),
            (Slot: 0, Move: QueueMove.Down)
        };

        foreach (var (slot, move) in moves)
        {
            await fixture.Queue.MoveAsync(fixture.ProfileId, slots[slot], move);
            await fixture.AssertContiguousAsync();
        }

        var final = await fixture.Queue.GetQueueAsync(fixture.ProfileId);

        // Nothing lost, nothing duplicated, however much it was shuffled.
        Assert.Equal(6, final.Count);
        Assert.Equal(["A", "B", "C", "D", "E", "F"], final.Select(s => s.Title).Order());
    }

    [Fact]
    public async Task A_drag_moves_a_slot_to_an_explicit_position()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C", "D", "E");

        Assert.True(await fixture.Queue.ReorderAsync(fixture.ProfileId, slots[0], targetPosition: 3));

        Assert.Equal("B C D A E", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    /// <summary>
    /// The browser's idea of the queue length can lag the server's, so a drop past
    /// the end means "last" rather than "reject this".
    /// </summary>
    [Fact]
    public async Task A_drag_past_the_end_lands_at_the_end()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C");

        Assert.True(await fixture.Queue.ReorderAsync(fixture.ProfileId, slots[0], targetPosition: 99));

        Assert.Equal("B C A", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    /// <summary>
    /// Positions are not unique in the schema (D2), so a queue can in principle
    /// arrive with duplicates or gaps. The next ordinary edit should repair it
    /// rather than compounding it.
    /// </summary>
    [Fact]
    public async Task A_non_contiguous_queue_is_repaired_by_the_next_edit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C");

        await using (var context = fixture.Database.CreateContext())
        {
            var items = await context.QueueItems.OrderBy(q => q.Id).ToListAsync();
            items[0].Position = 5;
            items[1].Position = 5;
            items[2].Position = 40;
            await context.SaveChangesAsync();
        }

        Assert.True(await fixture.Queue.MoveAsync(fixture.ProfileId, slots[2], QueueMove.Top));

        Assert.Equal("C A B", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    // --- Removal ---------------------------------------------------------

    [Fact]
    public async Task Removing_a_slot_closes_the_gap_it_leaves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B", "C", "D");

        Assert.True(await fixture.Queue.RemoveAsync(fixture.ProfileId, slots[1]));

        Assert.Equal("A C D", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    [Fact]
    public async Task Removing_a_slot_leaves_the_library_entry_alone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A");

        await fixture.Queue.RemoveAsync(fixture.ProfileId, slots[0]);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Removing_an_unknown_slot_reports_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueTitlesAsync("A");

        Assert.False(await fixture.Queue.RemoveAsync(fixture.ProfileId, queueItemId: 9999));
    }

    // --- Profile isolation -----------------------------------------------

    /// <summary>
    /// Multi-user is post-MVP, but every row already carries a ProfileId and the
    /// service takes one, so an id from another profile must be rejected rather than
    /// quietly acted on. Getting this wrong now would only surface much later.
    /// </summary>
    [Fact]
    public async Task A_slot_belonging_to_another_profile_cannot_be_touched()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slots = await fixture.QueueTitlesAsync("A", "B");

        await using var context = fixture.Database.CreateContext();
        var other = await SeedData.CreateProfileAsync(context, "Other");

        Assert.False(await fixture.Queue.MoveAsync(other.Id, slots[1], QueueMove.Top));
        Assert.False(await fixture.Queue.RemoveAsync(other.Id, slots[1]));
        Assert.Equal("A B", await fixture.OrderAsync());
    }

    // --- Adding ----------------------------------------------------------

    [Fact]
    public async Task Adding_appends_to_the_end_rather_than_guessing_at_priority()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueTitlesAsync("A", "B");
        await fixture.QueueTitlesAsync("C");

        Assert.Equal("A B C", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    [Fact]
    public async Task Adding_something_already_queued_is_a_no_op_rather_than_an_error()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueTitlesAsync("A");

        await using var context = fixture.Database.CreateContext();
        var animeId = await context.Anime.Select(a => a.Id).SingleAsync();

        var result = await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [animeId, animeId]);

        // One distinct title was asked for and skipped; the duplicate in the request
        // is not counted twice, or a selection bug would inflate the message shown.
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.AlreadyQueued);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("A", await fixture.OrderAsync());
    }

    [Fact]
    public async Task Adding_a_title_that_does_not_exist_creates_no_slot()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [4242]);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Unavailable);
        Assert.Empty(await fixture.Queue.GetQueueAsync(fixture.ProfileId));
    }

    /// <summary>
    /// Up Next holds what the user still intends to watch, so a title that has left
    /// Planning is declined rather than queued. This is the same rule
    /// <c>AdvanceAsync</c> applies later — without it, adding a finished show would
    /// create a slot that the next import silently deleted.
    /// </summary>
    [Theory]
    [InlineData(LibraryStatus.Watching)]
    [InlineData(LibraryStatus.Completed)]
    [InlineData(LibraryStatus.OnHold)]
    [InlineData(LibraryStatus.Dropped)]
    public async Task A_title_that_is_no_longer_planned_cannot_be_queued(LibraryStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();

        int animeId;
        await using (var context = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(context, "Gunbuster");
            animeId = anime.Id;

            var entry = SeedData.Entry(fixture.ProfileId, anime.Id);
            entry.Status = status;
            context.LibraryEntries.Add(entry);
            await context.SaveChangesAsync();
        }

        var result = await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [animeId]);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.NoLongerPlanned);
        Assert.Empty(await fixture.Queue.GetQueueAsync(fixture.ProfileId));
    }

    /// <summary>
    /// Not a special case carved out for re-watching — the ordinary rule, reached by
    /// the source saying the title is planned again (D12).
    /// </summary>
    [Fact]
    public async Task A_finished_title_becomes_queueable_again_once_it_is_planned_again()
    {
        await using var fixture = await Fixture.CreateAsync();

        int animeId;
        await using (var context = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(context, "Gunbuster");
            animeId = anime.Id;

            var entry = SeedData.Entry(fixture.ProfileId, anime.Id);
            entry.Status = LibraryStatus.Completed;
            context.LibraryEntries.Add(entry);
            await context.SaveChangesAsync();
        }

        Assert.Equal(0, (await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [animeId])).Added);

        await SetStatusAsync(fixture, animeId, LibraryStatus.Planning);

        Assert.Equal(1, (await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [animeId])).Added);
        Assert.Equal("Gunbuster", await fixture.OrderAsync());
    }

    [Fact]
    public async Task A_title_with_no_library_entry_cannot_be_queued()
    {
        // The queue orders the watch list; it does not add to it. Something absent
        // from the library has no intent recorded about it either way.
        await using var fixture = await Fixture.CreateAsync();

        int animeId;
        await using (var context = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(context, "Not in the library");
            animeId = anime.Id;
        }

        var result = await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [animeId]);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Unavailable);
    }

    /// <summary>
    /// The defect this rule closes: the two ways into the queue used to disagree.
    /// Expansion filtered on Planning; adding the same titles individually did not.
    /// </summary>
    [Fact]
    public async Task Both_ways_into_the_queue_apply_the_same_rule()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture, firstSeasonStatus: LibraryStatus.Completed);

        List<int> allMembers;
        await using (var context = fixture.Database.CreateContext())
        {
            allMembers = await context.Anime
                .Where(a => a.FranchiseId == franchiseId)
                .OrderBy(a => a.FranchiseOrder)
                .Select(a => a.Id)
                .ToListAsync();
        }

        // Every member, watched and optional alike, offered directly.
        var direct = await fixture.Queue.AddAnimeAsync(fixture.ProfileId, allMembers);
        var queuedDirectly = await fixture.OrderAsync();

        // The completed first season is refused by both paths. The optional special
        // is the one difference, and it is deliberate: expansion excludes it by
        // default, while asking for it by name is an explicit choice.
        Assert.Equal(1, direct.NoLongerPlanned);
        Assert.Equal("Slayers Next Slayers Try Slayers Special", queuedDirectly);

        await using var verify = fixture.Database.CreateContext();
        Assert.Equal(3, await verify.QueueItems.CountAsync());
    }

    // --- Franchise expansion (D15) ---------------------------------------

    /// <summary>
    /// Seeds a Slayers franchise: three ordinary seasons in viewing order plus one
    /// optional special, all Planning unless overridden.
    /// </summary>
    private static async Task<int> SeedSlayersAsync(
        Fixture fixture,
        LibraryStatus firstSeasonStatus = LibraryStatus.Planning)
    {
        await using var context = fixture.Database.CreateContext();

        var franchise = await SeedData.CreateFranchiseAsync(context, "Slayers");

        var titles = new[]
        {
            ("Slayers", 1, false),
            ("Slayers Next", 2, false),
            ("Slayers Try", 3, false),
            ("Slayers Special", 4, true)
        };

        var first = true;

        foreach (var (title, order, optional) in titles)
        {
            var anime = await SeedData.CreateAnimeAsync(context, title);
            anime.FranchiseId = franchise.Id;
            anime.FranchiseOrder = order;
            anime.OptionalWithinFranchise = optional;

            var entry = SeedData.Entry(fixture.ProfileId, anime.Id);
            entry.Status = first ? firstSeasonStatus : LibraryStatus.Planning;
            context.LibraryEntries.Add(entry);

            first = false;
        }

        await context.SaveChangesAsync();
        return franchise.Id;
    }

    /// <summary>
    /// The mechanic D15 replaces the franchise slot with. One click still expresses
    /// one decision, but what lands is a run of watchable titles in viewing order.
    /// </summary>
    [Fact]
    public async Task Queueing_a_franchise_appends_its_titles_individually_in_viewing_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        var result = await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        // Three, not four: the optional special is left out by default.
        Assert.Equal(3, result.Added);
        Assert.Equal("Slayers Slayers Next Slayers Try", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    [Fact]
    public async Task Queueing_a_franchise_can_include_its_optional_entries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId, includeOptional: true);

        Assert.Equal("Slayers Slayers Next Slayers Try Slayers Special", await fixture.OrderAsync());
    }

    [Fact]
    public async Task Queueing_a_franchise_skips_titles_already_watched()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture, firstSeasonStatus: LibraryStatus.Completed);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        Assert.Equal("Slayers Next Slayers Try", await fixture.OrderAsync());
    }

    /// <summary>
    /// What the whole change is for. Under the old model this was structurally
    /// impossible: one slot held the entire franchise, so nothing could go between
    /// two of its seasons.
    /// </summary>
    [Fact]
    public async Task Something_else_can_be_placed_between_two_seasons_of_a_franchise()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);
        await fixture.QueueTitlesAsync("Gunbuster");

        var queue = await fixture.Queue.GetQueueAsync(fixture.ProfileId);
        var gunbuster = queue.Single(s => s.Title == "Gunbuster");

        Assert.True(await fixture.Queue.ReorderAsync(fixture.ProfileId, gunbuster.QueueItemId, targetPosition: 1));

        Assert.Equal("Slayers Gunbuster Slayers Next Slayers Try", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    /// <summary>
    /// Each season releases on its own, so the next one rises to meet the user.
    /// Under the old model the whole franchise held position 1 until every entry
    /// had been watched.
    /// </summary>
    [Fact]
    public async Task Watching_one_season_advances_the_queue_to_the_next()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        await using (var context = fixture.Database.CreateContext())
        {
            var entry = await context.LibraryEntries
                .Include(e => e.Anime)
                .SingleAsync(e => e.Anime!.Title == "Slayers");

            entry.Status = LibraryStatus.Watching;
            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await fixture.Queue.AdvanceAsync(fixture.ProfileId));
        Assert.Equal("Slayers Next Slayers Try", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    [Fact]
    public async Task Re_adding_a_franchise_queues_only_what_is_not_already_there()
    {
        // What happens when sync brings a new season: one click, one new row.
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        await using (var context = fixture.Database.CreateContext())
        {
            var revolution = await SeedData.CreateAnimeAsync(context, "Slayers Revolution");
            revolution.FranchiseId = franchiseId;
            revolution.FranchiseOrder = 5;
            context.LibraryEntries.Add(SeedData.Entry(fixture.ProfileId, revolution.Id));
            await context.SaveChangesAsync();
        }

        var result = await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        Assert.Equal(1, result.Added);
        Assert.Equal("Slayers Slayers Next Slayers Try Slayers Revolution", await fixture.OrderAsync());
    }

    [Fact]
    public async Task Members_with_no_viewing_order_are_queued_last_rather_than_first()
    {
        // A null FranchiseOrder means nobody has said where it goes — which is not
        // a claim that it goes before the first season.
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        await using (var context = fixture.Database.CreateContext())
        {
            var unsequenced = await SeedData.CreateAnimeAsync(context, "Slayers Premium");
            unsequenced.FranchiseId = franchiseId;
            unsequenced.FranchiseOrder = null;
            context.LibraryEntries.Add(SeedData.Entry(fixture.ProfileId, unsequenced.Id));
            await context.SaveChangesAsync();
        }

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        Assert.Equal("Slayers Slayers Next Slayers Try Slayers Premium", await fixture.OrderAsync());
    }

    [Fact]
    public async Task A_franchise_with_nothing_left_to_queue_is_not_offered()
    {
        await using var fixture = await Fixture.CreateAsync();
        var slayersId = await SeedSlayersAsync(fixture);

        await using (var context = fixture.Database.CreateContext())
        {
            var other = await SeedData.CreateFranchiseAsync(context, "Patlabor");
            var patlabor = await SeedData.CreateAnimeAsync(context, "Patlabor");
            patlabor.FranchiseId = other.Id;
            context.LibraryEntries.Add(SeedData.Entry(fixture.ProfileId, patlabor.Id));

            // No titles at all, so nothing a click could add.
            await SeedData.CreateFranchiseAsync(context, "Nothing In Here");
            await context.SaveChangesAsync();
        }

        var before = await fixture.Queue.GetQueueableFranchisesAsync(fixture.ProfileId);

        // The count offered is what the click will actually add — three, not the
        // four titles Slayers contains, because one of them is optional.
        Assert.Equal(["Patlabor", "Slayers"], before.Select(f => f.Name).Order());
        Assert.Equal(3, before.Single(f => f.Name == "Slayers").QueueableCount);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, slayersId);

        // Now fully queued apart from its optional entry, so it drops off the list
        // rather than being offered and doing nothing.
        var after = await fixture.Queue.GetQueueableFranchisesAsync(fixture.ProfileId);
        Assert.Equal(["Patlabor"], after.Select(f => f.Name));

        // Unless the optional entries are asked for, which brings it back.
        var withOptional = await fixture.Queue.GetQueueableFranchisesAsync(
            fixture.ProfileId, includeOptional: true);

        Assert.Equal(1, withOptional.Single(f => f.Name == "Slayers").QueueableCount);
    }

    // --- Advancement (D12) -----------------------------------------------

    /// <summary>
    /// The rule that replaces the watching workflow: there is no "start watching"
    /// button, so a queue slot is released by observing that its title stopped
    /// being Planning somewhere else.
    /// </summary>
    [Theory]
    [InlineData(LibraryStatus.Watching)]
    [InlineData(LibraryStatus.Completed)]
    [InlineData(LibraryStatus.OnHold)]
    [InlineData(LibraryStatus.Dropped)]
    public async Task A_title_that_stops_being_planned_leaves_the_queue(LibraryStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueTitlesAsync("A", "B", "C");

        await using (var context = fixture.Database.CreateContext())
        {
            var entry = await context.LibraryEntries
                .Include(e => e.Anime)
                .SingleAsync(e => e.Anime!.Title == "B");

            entry.Status = status;
            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await fixture.Queue.AdvanceAsync(fixture.ProfileId));

        Assert.Equal("A C", await fixture.OrderAsync());
        await fixture.AssertContiguousAsync();
    }

    [Fact]
    public async Task Advancing_when_nothing_has_changed_does_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueTitlesAsync("A", "B");

        Assert.Equal(0, await fixture.Queue.AdvanceAsync(fixture.ProfileId));
        Assert.Equal("A B", await fixture.OrderAsync());

        // Idempotent: the Phase 5 sync will call this on a schedule, so running it
        // repeatedly against an unchanged library must stay a no-op.
        Assert.Equal(0, await fixture.Queue.AdvanceAsync(fixture.ProfileId));
        Assert.Equal("A B", await fixture.OrderAsync());
    }

    [Fact]
    public async Task Advancing_an_empty_queue_is_harmless()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Equal(0, await fixture.Queue.AdvanceAsync(fixture.ProfileId));
    }

    /// <summary>
    /// A slot is released on evidence that it is done, never on the absence of
    /// evidence. A queued title with no library entry is unknown, not watched.
    /// </summary>
    /// <remarks>
    /// The slot is written directly rather than through <c>AddAnimeAsync</c>, which
    /// now refuses to create it. Advancement still has to cope with the state: a
    /// library entry can be deleted after its title was queued, and older data
    /// predates the rule. Two guards against the same corruption is the right number
    /// when one of them is a delete.
    /// </remarks>
    [Fact]
    public async Task A_queued_title_with_no_library_entry_is_left_alone()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(context, "Orphan");
            context.QueueItems.Add(SeedData.QueueSlot(fixture.ProfileId, position: 0, anime.Id));
            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await fixture.Queue.AdvanceAsync(fixture.ProfileId));
        Assert.Equal("Orphan", await fixture.OrderAsync());
    }

    /// <summary>
    /// Dissolving a franchise is a decision about labels, not about the queue. Its
    /// titles stay queued in the order the user put them in — under the old model
    /// the franchise's slot was deleted along with it, silently taking that
    /// ordering with it.
    /// </summary>
    [Fact]
    public async Task Dissolving_a_franchise_leaves_its_titles_queued_in_place()
    {
        await using var fixture = await Fixture.CreateAsync();
        var franchiseId = await SeedSlayersAsync(fixture);

        await fixture.Queue.AddFranchiseAsync(fixture.ProfileId, franchiseId);

        await using (var context = fixture.Database.CreateContext())
        {
            context.Franchises.Remove(await context.Franchises.SingleAsync(f => f.Id == franchiseId));
            await context.SaveChangesAsync();
        }

        Assert.Equal("Slayers Slayers Next Slayers Try", await fixture.OrderAsync());
        Assert.Equal(0, await fixture.Queue.AdvanceAsync(fixture.ProfileId));

        // The badge is all that is lost.
        var queue = await fixture.Queue.GetQueueAsync(fixture.ProfileId);
        Assert.All(queue, slot => Assert.Null(slot.FranchiseName));
    }

    [Fact]
    public async Task Advancement_leaves_another_profiles_queue_untouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueTitlesAsync("A");

        await using var context = fixture.Database.CreateContext();
        var other = await SeedData.CreateProfileAsync(context, "Other");

        Assert.Equal(0, await fixture.Queue.AdvanceAsync(other.Id));
        Assert.Equal("A", await fixture.OrderAsync());
    }

    private static async Task SetStatusAsync(Fixture fixture, int animeId, LibraryStatus status)
    {
        await using var context = fixture.Database.CreateContext();

        var entry = await context.LibraryEntries
            .SingleAsync(e => e.ProfileId == fixture.ProfileId && e.AnimeId == animeId);

        entry.Status = status;
        await context.SaveChangesAsync();
    }

    // --- Reading ---------------------------------------------------------

    [Fact]
    public async Task A_title_slot_carries_what_the_page_needs_to_render_it()
    {
        await using var fixture = await Fixture.CreateAsync();

        await using (var context = fixture.Database.CreateContext())
        {
            var franchise = await SeedData.CreateFranchiseAsync(context, "Hinamatsuri");

            var anime = await SeedData.CreateAnimeAsync(context, "Hinamatsuri", AnimeSource.MyAnimeList, "36296");
            anime.MediaType = MediaType.Tv;
            anime.EpisodeCount = 12;
            anime.EpisodeDurationMinutes = 23;
            anime.ReleaseYear = 2018;
            anime.FranchiseId = franchise.Id;

            var entry = SeedData.Entry(fixture.ProfileId, anime.Id);
            entry.EpisodesWatched = 4;
            context.LibraryEntries.Add(entry);

            await context.SaveChangesAsync();
            await fixture.Queue.AddAnimeAsync(fixture.ProfileId, [anime.Id]);

            // Started after it was queued, and before the import that would release
            // it — which is exactly when the page has to render a status other than
            // Planning.
            entry.Status = LibraryStatus.Watching;
            await context.SaveChangesAsync();
        }

        var slot = Assert.Single(await fixture.Queue.GetQueueAsync(fixture.ProfileId));

        Assert.Equal(0, slot.Position);
        Assert.Equal("Hinamatsuri", slot.Title);
        Assert.Equal(LibraryStatus.Watching, slot.Status);
        Assert.Equal(4, slot.EpisodesWatched);
        Assert.Equal(12 * 23, slot.EstimatedRuntimeMinutes);
        Assert.NotEmpty(slot.SourceLinks);

        // The franchise's only presence in the queue: a name to badge the row with.
        Assert.Equal("Hinamatsuri", slot.FranchiseName);
    }

    [Fact]
    public async Task An_empty_queue_reads_as_empty_rather_than_failing()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Empty(await fixture.Queue.GetQueueAsync(fixture.ProfileId));
    }
}

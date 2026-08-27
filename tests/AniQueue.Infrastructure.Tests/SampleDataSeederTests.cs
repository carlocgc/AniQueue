using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Library;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Persistence.Seeding;
using AniQueue.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The sample data exists to make manual testing meaningful, so it
/// has to actually contain the situations the UI needs to handle. These tests
/// assert that rather than trusting the seeder's own log line.
/// </summary>
public class SampleDataSeederTests
{
    [Fact]
    public async Task Seeding_produces_every_state_the_dashboard_must_render()
    {
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var byStatus = await context.LibraryEntries
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count);

        Assert.True(byStatus.GetValueOrDefault(LibraryStatus.Completed) > 0, "needs completed titles");
        Assert.True(byStatus.GetValueOrDefault(LibraryStatus.Watching) > 0, "needs a title in progress");
        Assert.True(byStatus.GetValueOrDefault(LibraryStatus.Planning) > 0, "needs backlog titles");
    }

    [Fact]
    public async Task Completed_titles_span_a_range_of_scores()
    {
        // A recommendation model can only infer taste if it sees both ends. Seed
        // data that was uniformly 9s would make the AI workflow untestable.
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var scores = await context.LibraryEntries
            .Where(e => e.UserScore != null)
            .Select(e => e.UserScore!.Value)
            .ToListAsync();

        Assert.True(scores.Count >= 3);
        Assert.True(scores.Max() - scores.Min() >= 3, $"scores were too uniform: {string.Join(", ", scores)}");
    }

    [Fact]
    public async Task The_queue_interleaves_seasons_of_one_series_with_unrelated_titles()
    {
        // The arrangement D15 exists for: two seasons of one series with something
        // else deliberately between them. If this cannot be seeded, the queue model
        // has taken the ordering away from the user.
        //
        // Matched on title rather than on any stored grouping, because since D23
        // there is none: what makes these rows a series is AniList's relation data,
        // which the seeder writes as edges rather than as membership of anything.
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var slots = await context.QueueItems
            .OrderBy(q => q.Position)
            .Select(q => new { q.Position, q.Anime!.Title })
            .ToListAsync();

        Assert.Equal(Enumerable.Range(0, slots.Count), slots.Select(s => s.Position));

        var seasonPositions = slots
            .Where(s => s.Title.StartsWith("Slayers", StringComparison.Ordinal))
            .Select(s => s.Position)
            .ToList();

        Assert.True(seasonPositions.Count >= 2, "the seed needs at least two seasons of one series queued");

        // Not adjacent — something unrelated sits in the gap.
        Assert.True(
            seasonPositions.Max() - seasonPositions.Min() > seasonPositions.Count - 1,
            $"the seasons were queued as one contiguous block at {string.Join(", ", seasonPositions)}");
    }

    [Fact]
    public async Task The_applied_recommendation_run_is_mirrored_onto_library_entries()
    {
        // D4: the columns on LibraryEntry are a read cache of the applied run.
        // If they drift from the run, backlog sorting shows something the
        // recommendation history cannot explain.
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var run = await context.RecommendationRuns.Include(r => r.Items).SingleAsync();
        Assert.True(run.WasApplied);

        // No filtering: every ranked item is a title now, so every one of them must
        // have been mirrored. Skipping any was previously unavoidable (D16).
        Assert.NotEmpty(run.Items);

        foreach (var item in run.Items)
        {
            var entry = await context.LibraryEntries.SingleAsync(e => e.AnimeId == item.AnimeId);

            Assert.Equal(item.PredictedScore, entry.RecommendationScore);
            Assert.Equal(item.Confidence, entry.RecommendationConfidence);
            Assert.Equal(item.Reason, entry.RecommendationReason);
        }
    }

    /// <summary>
    /// The seed has to contain a graph, or the backlog's expansion cannot be looked
    /// at without syncing a real account first — and the inner loop is F5, not a
    /// network round trip (§13).
    /// </summary>
    [Fact]
    public async Task Seeding_produces_a_relation_graph_the_backlog_can_expand()
    {
        await using var database = await SeededDatabaseAsync();

        var relations = new RelationService(
            database.ContextFactory,
            new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance));

        await using var context = database.CreateContext();

        var seasonTwo = await context.Anime.SingleAsync(a => a.Title == "Slayers Next");

        var related = await relations.GetRelatedAsync(Profile.DefaultProfileId, seasonTwo.Id);

        // Season one is stated from season one's side and season three from season
        // three's, so finding both proves the reverse index and the inversion that
        // goes with it — the half of the graph a tidier seed would never exercise.
        Assert.Equal(
            [("Slayers", RelationType.Prequel), ("Slayers Try", RelationType.Sequel)],
            related.Select(r => (r.Title, r.Relation)));
    }

    /// <summary>
    /// The seeded identifiers are invented, so the backfill must never go and ask
    /// about them: a development start would spend a real rate limit on titles that
    /// do not exist.
    /// </summary>
    [Fact]
    public async Task Seeded_titles_are_marked_as_already_asked_about()
    {
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var unanswered = await context.AnimeExternalIds
            .Where(x => x.Source == AnimeSource.AniList && x.RelationsFetchedAt == null)
            .CountAsync();

        Assert.Equal(0, unanswered);
    }

    /// <summary>
    /// A seeded database says up front that AniList is not to be read.
    /// </summary>
    /// <remarks>
    /// Sample titles carry identifiers AniList does not issue, so a real list coming
    /// back without them is — correctly — reported as those titles having gone
    /// missing (D19). That is what got the automatic seeder deleted (D27). Sample
    /// data and a real account are alternatives, and this is the seeder saying so in
    /// the only place that can stop an unattended run: the database.
    /// </remarks>
    // A_seeded_database_leaves_anilist_sync_switched_off was deleted here rather than
    // rewritten, and the reason is worth keeping. It asserted a settings row the
    // seeder wrote; Phase 10a moved that setting into userconfig.json, which a seeder
    // writing a database cannot reach — and must not, because the sample and real
    // profiles shared one settings file, so seeding it would have switched off the
    // user's actual sync.
    //
    // What replaces the protection is not a row but a path: the sample launch profile
    // points at ./data/sample/, so it gets its own database and its own settings file
    // with no account in it. That is a launchSettings.json fact, not something this
    // suite can observe, and a test asserting it here would be asserting its own
    // fixture rather than the application.

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_anything()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await InitialiseAsync(database);

        var seeder = new SampleDataSeeder(database.ContextFactory, NullLogger<SampleDataSeeder>.Instance);
        await seeder.SeedAsync();
        await seeder.SeedAsync();

        await using var context = database.CreateContext();
        var titles = await context.Anime.Select(a => a.Title).ToListAsync();

        Assert.Equal(titles.Count, titles.Distinct().Count());
        Assert.Equal(1, await context.RecommendationRuns.CountAsync());
    }

    private static async Task<SqliteTestDatabase> SeededDatabaseAsync()
    {
        var database = await SqliteTestDatabase.CreateAsync();
        await InitialiseAsync(database);

        await new SampleDataSeeder(database.ContextFactory, NullLogger<SampleDataSeeder>.Instance)
            .SeedAsync();

        return database;
    }

    private static Task InitialiseAsync(SqliteTestDatabase database) =>
        new DatabaseInitializer(
            database.ContextFactory,
            Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();
}

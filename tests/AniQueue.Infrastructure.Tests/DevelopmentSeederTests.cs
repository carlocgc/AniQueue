using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The seed data exists to make development and manual testing meaningful, so it
/// has to actually contain the situations the UI needs to handle. These tests
/// assert that rather than trusting the seeder's own log line.
/// </summary>
public class DevelopmentSeederTests
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
    public async Task The_queue_interleaves_franchise_seasons_with_standalone_titles()
    {
        // The arrangement D15 exists for: two seasons of one franchise with
        // something else deliberately between them. If this cannot be seeded, the
        // queue model has taken the ordering away from the user.
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var slots = await context.QueueItems
            .OrderBy(q => q.Position)
            .Select(q => new { q.Position, q.Anime!.FranchiseId })
            .ToListAsync();

        Assert.Equal(Enumerable.Range(0, slots.Count), slots.Select(s => s.Position));

        var franchisePositions = slots.Where(s => s.FranchiseId is not null).Select(s => s.Position).ToList();

        Assert.True(franchisePositions.Count >= 2, "the seed needs at least two entries of one franchise queued");

        // Not adjacent — something standalone sits in the gap.
        Assert.True(
            franchisePositions.Max() - franchisePositions.Min() > franchisePositions.Count - 1,
            $"franchise entries were queued as one contiguous block at {string.Join(", ", franchisePositions)}");
    }

    [Fact]
    public async Task A_franchise_contains_optional_side_entries()
    {
        // Franchise completion maths is only interesting when some entries are
        // optional, so the seed has to include them.
        await using var database = await SeededDatabaseAsync();
        await using var context = database.CreateContext();

        var entries = await context.Anime.Where(a => a.FranchiseId != null).ToListAsync();

        Assert.Contains(entries, a => a.OptionalWithinFranchise);
        Assert.Contains(entries, a => !a.OptionalWithinFranchise);
        Assert.All(entries, a => Assert.NotNull(a.FranchiseOrder));
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

        foreach (var item in run.Items.Where(i => i.AnimeId is not null))
        {
            var entry = await context.LibraryEntries.SingleAsync(e => e.AnimeId == item.AnimeId);

            Assert.Equal(item.PredictedScore, entry.RecommendationScore);
            Assert.Equal(item.Confidence, entry.RecommendationConfidence);
            Assert.Equal(item.Reason, entry.RecommendationReason);
        }
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_anything()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await InitialiseAsync(database);

        var seeder = new DevelopmentSeeder(database.ContextFactory, NullLogger<DevelopmentSeeder>.Instance);
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

        await new DevelopmentSeeder(database.ContextFactory, NullLogger<DevelopmentSeeder>.Instance)
            .SeedAsync();

        return database;
    }

    private static Task InitialiseAsync(SqliteTestDatabase database) =>
        new DatabaseInitializer(
            database.ContextFactory,
            Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();
}

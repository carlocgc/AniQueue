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
    public async Task The_queue_interleaves_seasons_of_one_series_with_unrelated_titles()
    {
        // The arrangement D15 exists for: two seasons of one series with something
        // else deliberately between them. If this cannot be seeded, the queue model
        // has taken the ordering away from the user.
        //
        // Matched on title rather than on any stored grouping, because since D23
        // there is none: what makes these rows a series is AniList's relation data,
        // and the seeder does not invent local relationships to stand in for it.
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

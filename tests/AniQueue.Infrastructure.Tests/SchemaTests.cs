using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// Verifies that the migrations produce the schema the roadmap specifies. Indexes
/// are easy to drop accidentally when an entity configuration is edited, and the
/// resulting damage is a silent performance cliff rather than a failure — so they
/// are asserted explicitly.
/// </summary>
public class SchemaTests
{
    [Fact]
    public async Task Migrations_apply_to_a_fresh_database()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var pending = await context.Database.GetPendingMigrationsAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Empty(pending);
        Assert.NotEmpty(applied);
    }

    [Theory]
    // Import deduplication (ROADMAP.md §4).
    [InlineData("IX_Anime_Source_SourceAnimeId")]
    // One relationship per profile per title; what makes re-import idempotent.
    [InlineData("IX_LibraryEntries_ProfileId_AnimeId")]
    // Queue ordering reads (D2 — intentionally not unique).
    [InlineData("IX_QueueItems_ProfileId_Position")]
    // No title may occupy two queue slots. Since D15 a slot is always one title,
    // so this index is no longer filtered and there is no franchise counterpart.
    [InlineData("IX_QueueItems_ProfileId_AnimeId")]
    public async Task Expected_index_exists(string indexName)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();

        var indexes = await database.GetIndexNamesAsync();

        Assert.Contains(indexName, indexes);
    }

    /// <summary>
    /// D2 is a deliberate trade: the database does not defend queue contiguity, so
    /// this asserts the constraint really is absent. If someone "helpfully" adds
    /// it later, reorders will start aborting mid-transaction and this test
    /// explains why that happened.
    /// </summary>
    [Fact]
    public async Task Queue_position_index_is_not_unique()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var first = await SeedData.CreateAnimeAsync(context, "First");
        var second = await SeedData.CreateAnimeAsync(context, "Second");

        // Two rows transiently sharing a position is exactly what happens partway
        // through a block shift, and must not fail.
        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0, animeId: first.Id));
        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0, animeId: second.Id));

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.QueueItems.CountAsync());
    }
}

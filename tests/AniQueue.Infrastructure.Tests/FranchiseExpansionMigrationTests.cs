using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The data half of the D15 migration.
///
/// Every other test here starts from an empty database with all migrations
/// applied, which means the expansion SQL in <c>FranchisesAreNotQueueItems</c> runs
/// against nothing and proves nothing. These tests migrate to the version before
/// it, write the franchise slots that version allowed, and then migrate up — the
/// only way to find out whether an existing queue survives the change.
/// </summary>
public class FranchiseExpansionMigrationTests
{
    /// <summary>The last migration in which a queue slot could hold a franchise.</summary>
    private const string BeforeD15 = "20260816195543_DropManualPriority";

    private sealed class LegacyDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private LegacyDatabase(SqliteConnection connection, DbContextOptions<AniQueueDbContext> options)
        {
            _connection = connection;
            Options = options;
        }

        public DbContextOptions<AniQueueDbContext> Options { get; }

        public AniQueueDbContext CreateContext() => new(Options);

        /// <summary>A database at the schema version just before D15.</summary>
        public static async Task<LegacyDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AniQueueDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var context = new AniQueueDbContext(options);
            await context.GetService<IMigrator>().MigrateAsync(BeforeD15);

            return new LegacyDatabase(connection, options);
        }

        /// <summary>Applies everything after it, including the expansion.</summary>
        public async Task MigrateToLatestAsync()
        {
            await using var context = new AniQueueDbContext(Options);
            await context.Database.MigrateAsync();
        }

        /// <summary>
        /// Writes a queue slot the current model can no longer express, so it has to
        /// go in as SQL rather than through EF.
        /// </summary>
        public async Task InsertFranchiseSlotAsync(int profileId, int position, int franchiseId)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "QueueItems" ("ProfileId", "Position", "AnimeId", "FranchiseId", "AddedAt")
                VALUES ($profile, $position, NULL, $franchise, '2026-01-01 00:00:00+00:00');
                """;

            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$franchise", franchiseId);

            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Writes a library entry as SQL rather than through EF.
        /// </summary>
        /// <remarks>
        /// EF issues an INSERT naming every column the *current* model maps, so
        /// seeding an old schema through it only works while no column has been
        /// added since. That assumption broke the moment D18 added
        /// LastWrittenBySource. Naming the columns explicitly pins this to the
        /// schema the test actually migrated to, which is the same reason the
        /// franchise slot above goes in as SQL.
        /// </remarks>
        public async Task InsertLibraryEntryAsync(int profileId, int animeId, LibraryStatus status)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "LibraryEntries"
                    ("ProfileId", "AnimeId", "Status", "EpisodesWatched", "IsHidden", "DateAdded", "LastUpdated")
                VALUES
                    ($profile, $anime, $status, 0, 0, '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                """;

            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$anime", animeId);
            command.Parameters.AddWithValue("$status", (int)status);

            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed record Seeded(int ProfileId, int FranchiseId, Dictionary<string, int> AnimeIds);

    private static async Task<Seeded> SeedFranchiseAsync(
        LegacyDatabase database,
        params (string Title, int? Order, bool Optional, LibraryStatus Status)[] members)
    {
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var franchise = await SeedData.CreateFranchiseAsync(context, "Slayers");
        var ids = new Dictionary<string, int>();

        foreach (var (title, order, optional, status) in members)
        {
            var anime = await SeedData.CreateAnimeAsync(context, title);
            anime.FranchiseId = franchise.Id;
            anime.FranchiseOrder = order;
            anime.OptionalWithinFranchise = optional;

            ids[title] = anime.Id;
        }

        await context.SaveChangesAsync();

        // After the catalogue rows are saved, because the entries reference them
        // and go in outside the change tracker.
        foreach (var (title, _, _, status) in members)
        {
            await database.InsertLibraryEntryAsync(profile.Id, ids[title], status);
        }

        return new Seeded(profile.Id, franchise.Id, ids);
    }

    /// <summary>The queue in display order, which is all the ordering has to preserve.</summary>
    private static async Task<List<string>> QueuedTitlesAsync(LegacyDatabase database)
    {
        await using var context = database.CreateContext();

        return await context.QueueItems
            .AsNoTracking()
            .OrderBy(q => q.Position)
            .Select(q => q.Anime!.Title)
            .ToListAsync();
    }

    [Fact]
    public async Task A_queued_franchise_becomes_its_titles_in_viewing_order()
    {
        await using var database = await LegacyDatabase.CreateAsync();

        var seeded = await SeedFranchiseAsync(
            database,
            ("Slayers", 1, false, LibraryStatus.Planning),
            ("Slayers Next", 2, false, LibraryStatus.Planning),
            ("Slayers Try", 3, false, LibraryStatus.Planning));

        await database.InsertFranchiseSlotAsync(seeded.ProfileId, position: 0, seeded.FranchiseId);

        await database.MigrateToLatestAsync();

        Assert.Equal(["Slayers", "Slayers Next", "Slayers Try"], await QueuedTitlesAsync(database));
    }

    [Fact]
    public async Task Expansion_applies_the_same_filters_the_application_does()
    {
        await using var database = await LegacyDatabase.CreateAsync();

        var seeded = await SeedFranchiseAsync(
            database,
            ("Slayers", 1, false, LibraryStatus.Completed),      // already watched
            ("Slayers Next", 2, false, LibraryStatus.Planning),
            ("Slayers Special", 3, true, LibraryStatus.Planning), // optional
            ("Slayers Try", 4, false, LibraryStatus.Planning));

        await database.InsertFranchiseSlotAsync(seeded.ProfileId, position: 0, seeded.FranchiseId);

        await database.MigrateToLatestAsync();

        Assert.Equal(["Slayers Next", "Slayers Try"], await QueuedTitlesAsync(database));
    }

    [Fact]
    public async Task A_title_queued_both_individually_and_via_its_franchise_is_not_duplicated()
    {
        // Would otherwise violate the unique index the migration creates, failing
        // the whole upgrade on a queue that was perfectly legal beforehand.
        await using var database = await LegacyDatabase.CreateAsync();

        var seeded = await SeedFranchiseAsync(
            database,
            ("Slayers", 1, false, LibraryStatus.Planning),
            ("Slayers Next", 2, false, LibraryStatus.Planning));

        await using (var context = database.CreateContext())
        {
            context.QueueItems.Add(
                SeedData.QueueSlot(seeded.ProfileId, position: 0, seeded.AnimeIds["Slayers"]));

            await context.SaveChangesAsync();
        }

        await database.InsertFranchiseSlotAsync(seeded.ProfileId, position: 1, seeded.FranchiseId);

        await database.MigrateToLatestAsync();

        Assert.Equal(["Slayers", "Slayers Next"], await QueuedTitlesAsync(database));
    }

    [Fact]
    public async Task Standalone_slots_keep_their_order_around_an_expanded_franchise()
    {
        await using var database = await LegacyDatabase.CreateAsync();

        var seeded = await SeedFranchiseAsync(
            database,
            ("Slayers", 1, false, LibraryStatus.Planning),
            ("Slayers Next", 2, false, LibraryStatus.Planning));

        await using (var context = database.CreateContext())
        {
            var first = await SeedData.CreateAnimeAsync(context, "Gunbuster");
            var last = await SeedData.CreateAnimeAsync(context, "Nichijou");

            context.QueueItems.Add(SeedData.QueueSlot(seeded.ProfileId, position: 0, first.Id));
            context.QueueItems.Add(SeedData.QueueSlot(seeded.ProfileId, position: 2, last.Id));
            await context.SaveChangesAsync();

            // Entries go in as SQL for the same reason as everywhere else here: the
            // current model maps columns this schema version does not have.
            await database.InsertLibraryEntryAsync(seeded.ProfileId, first.Id, LibraryStatus.Planning);
            await database.InsertLibraryEntryAsync(seeded.ProfileId, last.Id, LibraryStatus.Planning);
        }

        await database.InsertFranchiseSlotAsync(seeded.ProfileId, position: 1, seeded.FranchiseId);

        await database.MigrateToLatestAsync();

        // The expanded titles land at the end rather than in the gap the franchise
        // occupied. Documented and accepted: the migration preserves membership, and
        // the user reorders from there. Nothing is lost, which is the bar.
        Assert.Equal(
            ["Gunbuster", "Nichijou", "Slayers", "Slayers Next"],
            await QueuedTitlesAsync(database));
    }

    [Fact]
    public async Task A_franchise_with_nothing_left_to_watch_simply_disappears()
    {
        await using var database = await LegacyDatabase.CreateAsync();

        var seeded = await SeedFranchiseAsync(
            database,
            ("Slayers", 1, false, LibraryStatus.Completed),
            ("Slayers Next", 2, false, LibraryStatus.Completed));

        await database.InsertFranchiseSlotAsync(seeded.ProfileId, position: 0, seeded.FranchiseId);

        await database.MigrateToLatestAsync();

        Assert.Empty(await QueuedTitlesAsync(database));
    }
}

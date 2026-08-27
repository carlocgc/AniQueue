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
///
/// They still matter after D23 retired franchises, and arguably matter more: an
/// upgrade from a pre-D15 database now passes through both migrations in one run,
/// so what these assert is that a queue built under the oldest model survives
/// expansion and the subsequent drop with its ordering intact. Everything is
/// seeded as SQL, because the current model can express none of it.
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
        /// Writes a franchise, which the current model no longer has at all (D23),
        /// so it goes in as SQL against the schema this test migrated to.
        /// </summary>
        public async Task<int> InsertFranchiseAsync(string name)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "Franchises" ("Name", "ManualSortOrder", "CreatedAt", "UpdatedAt")
                VALUES ($name, 0, '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$name", name);

            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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

        /// <summary>
        /// Writes a catalogue row as SQL, for the same reason the entry above is.
        /// </summary>
        /// <remarks>
        /// The assumption broke a second time when the title variants replaced
        /// AlternativeTitle, so the columns are named explicitly here too. A test
        /// that seeds an old schema has to speak that schema.
        /// </remarks>
        public async Task<int> InsertAnimeAsync(string title, int? franchiseId, int? order, bool optional)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "Anime"
                    ("Title", "MediaType", "Source", "FranchiseId", "FranchiseOrder",
                     "OptionalWithinFranchise", "CreatedAt", "UpdatedAt")
                VALUES
                    ($title, 0, 0, $franchise, $order, $optional,
                     '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$franchise", (object?)franchiseId ?? DBNull.Value);
            command.Parameters.AddWithValue("$order", (object?)order ?? DBNull.Value);
            command.Parameters.AddWithValue("$optional", optional ? 1 : 0);

            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Writes the profile as SQL, for the third time the same assumption has
        /// broken.
        /// </summary>
        /// <remarks>
        /// It broke for the library entry when D18 added LastWrittenBySource, for the
        /// catalogue row when the title variants replaced AlternativeTitle, and here
        /// when D50 added LibraryKey to Profiles. The lesson is the one already stated
        /// twice above and evidently worth stating a third time: EF names every column
        /// the <i>current</i> model maps, so nothing seeded into an old schema may go
        /// through it. The remaining EF writes in this file all target tables the
        /// migrations under test create, and would fail the same way if those gained a
        /// column.
        /// </remarks>
        public async Task<int> InsertProfileAsync(string name)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "Profiles" ("Name", "CreatedAt")
                VALUES ($name, '2026-01-01 00:00:00+00:00');
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$name", name);

            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed record Seeded(int ProfileId, int FranchiseId, Dictionary<string, int> AnimeIds);

    private static async Task<Seeded> SeedFranchiseAsync(
        LegacyDatabase database,
        params (string Title, int? Order, bool Optional, LibraryStatus Status)[] members)
    {
        var profileId = await database.InsertProfileAsync("Test");
        var franchiseId = await database.InsertFranchiseAsync("Slayers");
        var ids = new Dictionary<string, int>();

        foreach (var (title, order, optional, _) in members)
        {
            ids[title] = await database.InsertAnimeAsync(title, franchiseId, order, optional);
        }

        // After the catalogue rows are saved, because the entries reference them
        // and go in outside the change tracker.
        foreach (var (title, _, _, status) in members)
        {
            await database.InsertLibraryEntryAsync(profileId, ids[title], status);
        }

        return new Seeded(profileId, franchiseId, ids);
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

        // Catalogue rows and entries both go in as SQL, for the same reason as
        // everywhere else here: the current model maps columns this schema version
        // does not have.
        // Standalone: no franchise, so the expansion has nothing to do with them and
        // their positions are what the test is about.
        var firstId = await database.InsertAnimeAsync("Gunbuster", franchiseId: null, order: null, optional: false);
        var lastId = await database.InsertAnimeAsync("Nichijou", franchiseId: null, order: null, optional: false);

        await using (var context = database.CreateContext())
        {
            context.QueueItems.Add(SeedData.QueueSlot(seeded.ProfileId, position: 0, firstId));
            context.QueueItems.Add(SeedData.QueueSlot(seeded.ProfileId, position: 2, lastId));
            await context.SaveChangesAsync();
        }

        await database.InsertLibraryEntryAsync(seeded.ProfileId, firstId, LibraryStatus.Planning);
        await database.InsertLibraryEntryAsync(seeded.ProfileId, lastId, LibraryStatus.Planning);

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

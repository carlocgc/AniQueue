using AniQueue.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// A real SQLite database, held in memory, with the real migrations applied.
///
/// The EF <c>InMemory</c> provider is deliberately not used anywhere in this
/// project: it enforces neither check constraints nor filtered unique indexes,
/// which are exactly what these tests exist to verify. A test that passes against
/// InMemory would prove nothing about the schema that actually ships.
///
/// An in-memory SQLite database lives only as long as a connection to it is open,
/// so this class holds one open for its lifetime and hands out contexts that share
/// it. Letting the connection close would silently discard the schema.
/// </summary>
public sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AniQueueDbContext> _options;

    private SqliteTestDatabase(SqliteConnection connection, DbContextOptions<AniQueueDbContext> options)
    {
        _connection = connection;
        _options = options;
        ContextFactory = new PooledFactory(options);
    }

    /// <summary>Mirrors how production code obtains contexts (D3).</summary>
    public IDbContextFactory<AniQueueDbContext> ContextFactory { get; }

    public static async Task<SqliteTestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        // Foreign keys are off by default in SQLite; without this the cascade
        // rules under test would be advisory only.
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AniQueueDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new AniQueueDbContext(options))
        {
            // Migrate, not EnsureCreated: this asserts the migrations themselves
            // produce a working schema, which is what production will run.
            await context.Database.MigrateAsync(cancellationToken);
        }

        return new SqliteTestDatabase(connection, options);
    }

    public AniQueueDbContext CreateContext() => new(_options);

    /// <summary>Names of every index in the database, from SQLite's own catalogue.</summary>
    public async Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name IS NOT NULL;";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class PooledFactory(DbContextOptions<AniQueueDbContext> options)
        : IDbContextFactory<AniQueueDbContext>
    {
        public AniQueueDbContext CreateDbContext() => new(options);
    }
}

using AniQueue.Core.Import;
using AniQueue.Infrastructure.Import;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Queue;
using AniQueue.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// A real import service over a real migrated database.
/// </summary>
/// <remarks>
/// Shared rather than nested because two suites need it: the file-import tests,
/// and the external-identity tests that go through the <see cref="ParseResult"/>
/// seam a sync will use.
/// </remarks>
internal sealed class ImportFixture : IAsyncDisposable
{
    public required SqliteTestDatabase Database { get; init; }

    public required IImportService Service { get; init; }

    /// <summary>The configuration the import reads precedence from.</summary>
    public required SyncOptions Options { get; init; }

    public static async Task<ImportFixture> CreateAsync()
    {
        var database = await SqliteTestDatabase.CreateAsync();

        await new DatabaseInitializer(
            database.ContextFactory,
            Microsoft.Extensions.Options.Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

        var options = new SyncOptions();

        return new ImportFixture
        {
            Database = database,
            Options = options,

            // The real queue service, not a stub: committing an import advances
            // the queue, and that is behaviour worth exercising here
            // rather than mocking away.
            Service = new ImportService(
                database.ContextFactory,
                new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance),

                // AniList holds the seat by default, which is the state a fresh
                // install is in. Precedence only decides contests between two sources
                // that both describe one title, so a test using a single source is
                // unaffected by it; one where a second source must win says so.
                new StubOptionsMonitor(options),
                NullLogger<ImportService>.Instance)
        };
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

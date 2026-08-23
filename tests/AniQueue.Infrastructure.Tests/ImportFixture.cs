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

    /// <summary>The configuration the import reads precedence from (Phase 10a).</summary>
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
            // the queue (D12), and that is behaviour worth exercising here
            // rather than mocking away.
            Service = new ImportService(
                database.ContextFactory,
                new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance),

                // No primary source by default, which is the state a fresh install is
                // in and the one every test here but the precedence suite wants: an
                // empty map means precedence never fires (D29).
                new StubOptionsMonitor(options),
                NullLogger<ImportService>.Instance)
        };
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

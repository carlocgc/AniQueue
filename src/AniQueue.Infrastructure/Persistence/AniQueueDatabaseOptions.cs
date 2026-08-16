using Microsoft.Data.Sqlite;

namespace AniQueue.Infrastructure.Persistence;

/// <summary>
/// Where the database lives and how connections to it behave.
/// </summary>
public class AniQueueDatabaseOptions
{
    /// <summary>Configuration section name, e.g. <c>Database:Path</c>.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Path to the SQLite file. Defaults to the container's persistent volume;
    /// local development overrides this to a path inside the repository.
    /// </summary>
    public string Path { get; set; } = "/data/aniqueue.db";

    /// <summary>
    /// How long to keep retrying when the database is locked by another writer.
    /// SQLite permits one writer at a time, so a long-running import overlapping
    /// with a queue write needs to wait rather than fail outright.
    /// </summary>
    public int BusyTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// True when configured for an in-memory database, which several behaviours
    /// (directory creation, WAL) must skip. Used by tests.
    /// </summary>
    public bool IsInMemory =>
        Path.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
        || Path.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);

    public string BuildConnectionString() =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path,

            // SQLite leaves foreign key enforcement off unless asked. Without this
            // the cascade rules in the entity configurations would be advisory.
            ForeignKeys = true,

            // Microsoft.Data.Sqlite retries on SQLITE_BUSY until this elapses.
            DefaultTimeout = BusyTimeoutSeconds
        }.ToString();
}

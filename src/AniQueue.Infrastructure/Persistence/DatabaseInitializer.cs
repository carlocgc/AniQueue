using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Persistence;

/// <summary>
/// Brings the database up to date at startup: applies pending migrations, sets
/// the connection pragmas, and guarantees the default profile exists.
///
/// Failures here are fatal by design. Starting a web server that cannot reach its
/// database only converts one clear startup error into an unbounded stream of
/// confusing request errors.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IOptions<AniQueueDatabaseOptions> options,
    ILogger<DatabaseInitializer> logger)
{
    private readonly AniQueueDatabaseOptions _options = options.Value;

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseDirectoryExists();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await ApplyMigrationsAsync(context, cancellationToken);
        await EnableWriteAheadLoggingAsync(context, cancellationToken);
        await EnsureDefaultProfileAsync(context, cancellationToken);
    }

    /// <summary>
    /// SQLite will not create missing intermediate directories, and a bind-mounted
    /// volume may be empty on first run.
    /// </summary>
    private void EnsureDatabaseDirectoryExists()
    {
        if (_options.IsInMemory)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_options.Path));
        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory))
        {
            return;
        }

        logger.LogInformation("Creating database directory {DatabaseDirectory}", directory);
        Directory.CreateDirectory(directory);
    }

    private async Task ApplyMigrationsAsync(AniQueueDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            if (pending.Length == 0)
            {
                logger.LogInformation("Database schema is up to date; no migrations pending");
            }
            else
            {
                logger.LogInformation(
                    "Applying {PendingMigrationCount} pending migration(s): {PendingMigrations}",
                    pending.Length,
                    string.Join(", ", pending));
            }

            await context.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The path is included because "unable to open database file" is the
            // most common failure and is almost always a volume permission problem.
            logger.LogCritical(
                ex,
                "Failed to apply database migrations for {DatabasePath}. If this is a bind mount, "
                + "check that the directory exists and is writable by the container user",
                _options.Path);
            throw;
        }
    }

    /// <summary>
    /// Write-ahead logging lets reads proceed during a write, which matters when a
    /// long import overlaps with ordinary browsing. The setting is stored in the
    /// database file itself, so applying it once per startup is sufficient.
    /// </summary>
    private async Task EnableWriteAheadLoggingAsync(AniQueueDbContext context, CancellationToken cancellationToken)
    {
        if (_options.IsInMemory)
        {
            // WAL requires a real file; in-memory databases reject it.
            return;
        }

        var mode = await context.Database
            .SqlQueryRaw<string>("PRAGMA journal_mode=WAL;")
            .ToListAsync(cancellationToken);

        var applied = mode.FirstOrDefault();
        if (!string.Equals(applied, "wal", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Requested WAL journal mode but the database reports {JournalMode}", applied);
        }
    }

    /// <summary>
    /// The MVP has no registration flow, so the single profile has to exist before
    /// anything can reference it.
    /// </summary>
    private async Task EnsureDefaultProfileAsync(AniQueueDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Profiles.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var profile = new Profile
        {
            Id = Profile.DefaultProfileId,
            Name = "Default",
            CreatedAt = now,
            Settings = new ProfileSettings { DisplayName = "Default" }
        };

        context.Profiles.Add(profile);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created default profile {ProfileId}", profile.Id);
    }
}

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
        await EnsureLibraryKeysAsync(context, cancellationToken);
        await EnsureSecurityStampsAsync(context, cancellationToken);
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
            LibraryKey = Profile.NewLibraryKey(),
            SecurityStamp = Profile.NewSecurityStamp(),
            Settings = new ProfileSettings { DisplayName = "Default" }
        };

        context.Profiles.Add(profile);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created default profile {ProfileId}", profile.Id);
    }

    /// <summary>
    /// Gives every profile a library key, which is what lets a scoring reply say
    /// which library it was generated against.
    /// </summary>
    /// <remarks>
    /// Separate from the profile creation above because it has to reach rows that
    /// creation never touches: a database that predates the column has a profile with
    /// no key, and it is the same database whose replies most need naming. Running
    /// unconditionally on every start also means a row inserted by a future path that
    /// forgets the key is repaired rather than left to produce replies nothing can
    /// check.
    ///
    /// A key is never regenerated. Doing so would invalidate every reply a user is
    /// holding, which is the failure this exists to report rather than to cause.
    /// </remarks>
    private async Task EnsureLibraryKeysAsync(AniQueueDbContext context, CancellationToken cancellationToken)
    {
        var unnamed = await context.Profiles
            .Where(p => p.LibraryKey == null || p.LibraryKey == "")
            .ToListAsync(cancellationToken);

        if (unnamed.Count == 0)
        {
            return;
        }

        foreach (var profile in unnamed)
        {
            profile.LibraryKey = Profile.NewLibraryKey();
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Assigned a library key to {ProfileCount} profile(s) that had none",
            unnamed.Count);
    }

    /// <summary>
    /// Gives every profile a security stamp, so no sign-in path ever has to write
    /// to mint one.
    /// </summary>
    /// <remarks>
    /// Filled whether or not a password is set. A stamp is what a cookie carries so
    /// it can be retired, and minting one lazily would mean a database write on the
    /// path that checks a password — which is the path a wrong password takes too.
    ///
    /// Unlike a library key this may be replaced, and setting or clearing a password
    /// is what replaces it. This only ever fills an empty one.
    /// </remarks>
    private async Task EnsureSecurityStampsAsync(AniQueueDbContext context, CancellationToken cancellationToken)
    {
        var unstamped = await context.Profiles
            .Where(p => p.SecurityStamp == null || p.SecurityStamp == "")
            .ToListAsync(cancellationToken);

        if (unstamped.Count == 0)
        {
            return;
        }

        foreach (var profile in unstamped)
        {
            profile.SecurityStamp = Profile.NewSecurityStamp();
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Assigned a security stamp to {ProfileCount} profile(s) that had none",
            unstamped.Count);
    }
}

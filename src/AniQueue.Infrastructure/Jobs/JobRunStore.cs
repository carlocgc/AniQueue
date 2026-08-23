using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Jobs;

/// <summary>
/// <see cref="JobRun"/> against the database, with a bounded history.
/// </summary>
public sealed class JobRunStore(IDbContextFactory<AniQueueDbContext> contextFactory) : IJobRunStore
{
    /// <summary>
    /// How many runs are kept per unit.
    /// </summary>
    /// <remarks>
    /// Two hundred, pruned on insert. Five tasks makes a thousand rows and a few
    /// hundred kilobytes — small enough that no cleaner job, retention setting or
    /// scheduled sweep is worth existing, which is the whole reason for choosing a
    /// count rather than an age.
    ///
    /// It is a count of runs and not of days on purpose. A task on a daily cadence
    /// keeps most of a year; one that wakes on every library change keeps a few busy
    /// days. Both are "enough to see what has been happening", which is the question
    /// this history exists to answer.
    /// </remarks>
    public const int Retained = 200;

    /// <summary>What a unitless task's runs are filed under.</summary>
    /// <remarks>
    /// Empty rather than null so that every lookup is an equality test. A nullable
    /// column compared against a nullable parameter becomes <c>= @p</c>, which is
    /// never true of NULL — and since every read here is by unit, that would be a
    /// permanent and silent miss.
    /// </remarks>
    private static string Normalise(string? unitKey) => unitKey ?? string.Empty;

    public async Task RecordAsync(JobRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        run.UnitKey = Normalise(run.UnitKey);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        context.JobRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);

        await PruneAsync(context, run.TaskKey, run.UnitKey, cancellationToken);
    }

    public async Task<DateTimeOffset?> LastRunAtAsync(
        string taskKey,
        string? unitKey,
        CancellationToken cancellationToken = default)
    {
        var unit = Normalise(unitKey);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Ordered by key rather than by StartedAt, because SQLite can neither order
        // nor compare a DateTimeOffset. The table is only ever appended to, so the key
        // is insertion order and insertion order is chronological — the same
        // workaround SyncRun already uses for the same reason.
        var run = await context.JobRuns
            .AsNoTracking()
            .Where(r => r.TaskKey == taskKey && r.UnitKey == unit)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.StartedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return run?.StartedAt;
    }

    /// <summary>
    /// Drops everything older than the newest <see cref="Retained"/> for this unit.
    /// </summary>
    /// <remarks>
    /// Two statements rather than one, because SQLite has no <c>DELETE … LIMIT</c> and
    /// a correlated subquery over the same table it is deleting from is the kind of
    /// thing that reads clever and behaves differently between providers. Finding the
    /// oldest key worth keeping and deleting below it is one indexed seek and one
    /// ranged delete, and both are covered by the index this table already has.
    ///
    /// Pruned per unit rather than per task, so a source that syncs hourly cannot
    /// crowd out the history of one that syncs weekly.
    /// </remarks>
    private static async Task PruneAsync(
        AniQueueDbContext context,
        string taskKey,
        string unitKey,
        CancellationToken cancellationToken)
    {
        var oldestKept = await context.JobRuns
            .AsNoTracking()
            .Where(r => r.TaskKey == taskKey && r.UnitKey == unitKey)
            .OrderByDescending(r => r.Id)
            .Skip(Retained - 1)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (oldestKept == 0)
        {
            return;
        }

        await context.JobRuns
            .Where(r => r.TaskKey == taskKey && r.UnitKey == unitKey && r.Id < oldestKept)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

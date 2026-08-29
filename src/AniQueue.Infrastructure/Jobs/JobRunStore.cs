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

    /// <summary>
    /// Writes a run down, replacing the no-op it supersedes.
    /// </summary>
    /// <remarks>
    /// <b>Consecutive no-op runs collapse to the latest one.</b> A task that gates on
    /// its own precondition rather than on the cadence — cover art, relations — runs
    /// on every tick and on every library change, and in its converged state that is
    /// a hundred runs a day that all say the same thing. Kept one row each, they fill
    /// the retained history within two days and the runs that did something are
    /// pruned away underneath them.
    ///
    /// What the row says is unchanged, and that is the point: "checked forty minutes
    /// ago, nothing to do" is still there, still current, and still tells a converged
    /// task apart from a broken one. What goes is the ninety-five identical rows
    /// underneath it.
    ///
    /// <b>Superseded by deleting and re-inserting rather than by updating in place.</b>
    /// Every read here orders by <c>Id</c>, because SQLite can neither order nor
    /// compare a <c>DateTimeOffset</c> — so a row updated in place would keep its old
    /// key and sort below runs that happened before it, and the history would show
    /// "just now" underneath "three hours ago".
    /// </remarks>
    public async Task RecordAsync(JobRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        run.UnitKey = Normalise(run.UnitKey);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Read before the insert, so the row this one supersedes is identified while
        // it is still the newest.
        var superseded = run.Outcome is JobOutcome.NothingToDo
            ? await NewestNoOpIdAsync(context, run.TaskKey, run.UnitKey, cancellationToken)
            : null;

        context.JobRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);

        // After the insert rather than before it. An interruption between the two
        // leaves one no-op row too many, which the next one collapses; the other order
        // would lose the run the cadence clock is measured from.
        if (superseded is { } id)
        {
            await context.JobRuns
                .Where(r => r.Id == id)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await PruneAsync(context, run.TaskKey, run.UnitKey, cancellationToken);
    }

    /// <summary>
    /// The newest run for this unit, if it is itself a no-op. Null otherwise.
    /// </summary>
    private static async Task<int?> NewestNoOpIdAsync(
        AniQueueDbContext context,
        string taskKey,
        string unitKey,
        CancellationToken cancellationToken)
    {
        var newest = await context.JobRuns
            .AsNoTracking()
            .Where(r => r.TaskKey == taskKey && r.UnitKey == unitKey)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Id, r.Outcome })
            .FirstOrDefaultAsync(cancellationToken);

        return newest is { Outcome: JobOutcome.NothingToDo } ? newest.Id : null;
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

    public async Task<IReadOnlyDictionary<(string TaskKey, string UnitKey), JobRun>> LatestAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Grouped and maxed in the database, then fetched by key. The alternative —
        // pulling every row and picking in memory — is bounded by pruning and would
        // still read two hundred rows per unit to answer a question about one.
        var newest = await context.JobRuns
            .AsNoTracking()
            .GroupBy(r => new { r.TaskKey, r.UnitKey })
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(cancellationToken);

        var runs = await context.JobRuns
            .AsNoTracking()
            .Where(r => newest.Contains(r.Id))
            .ToListAsync(cancellationToken);

        return runs.ToDictionary(r => (r.TaskKey, r.UnitKey));
    }

    public async Task<IReadOnlyList<JobRun>> RecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // By key, for the reason everything here is: SQLite cannot order a
        // DateTimeOffset, and an append-only table's key is its chronology.
        return await context.JobRuns
            .AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
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

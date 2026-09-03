using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Infrastructure.Jobs;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The run record against a real database, because what it has to get right is
/// storage: ordering without a sortable timestamp, pruning per unit, and a lookup
/// that has to match an empty key rather than a null one.
/// </summary>
public class JobRunStoreTests
{
    private const string Task = "sync";

    private static JobRun Run(
        string? unit = null,
        JobOutcome outcome = JobOutcome.Succeeded,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            TaskKey = Task,
            UnitKey = unit ?? string.Empty,
            Trigger = JobTrigger.Timer,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Outcome = outcome
        };

    [Fact]
    public async Task A_unit_that_has_never_run_has_no_last_run()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        Assert.Null(await store.LastRunAtAsync(Task, "AniList"));
    }

    /// <summary>
    /// The last run is the newest by key, because there is no orderable timestamp.
    /// </summary>
    /// <remarks>
    /// The two runs are given <em>descending</em> start times on purpose. SQLite can
    /// neither order nor compare a <c>DateTimeOffset</c>, so recency is read from the
    /// key — and a test whose rows happened to be in timestamp order too would pass
    /// against an implementation that sorted the wrong column.
    /// </remarks>
    [Fact]
    public async Task The_newest_run_is_the_one_inserted_last()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        var newest = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

        await store.RecordAsync(Run("AniList", startedAt: newest.AddHours(5)));
        await store.RecordAsync(Run("AniList", startedAt: newest));

        Assert.Equal(newest, await store.LastRunAtAsync(Task, "AniList"));
    }

    /// <summary>
    /// A task with one unit files its runs under an empty key, and finds them again.
    /// </summary>
    /// <remarks>
    /// The reason the column is not nullable. EF translates a comparison against a
    /// nullable parameter to <c>= @p</c>, which is never true of NULL — and since
    /// every read here is by unit, a null key would miss its own rows silently and
    /// the cadence would restart the task on every tick.
    /// </remarks>
    [Fact]
    public async Task A_task_with_one_unit_finds_its_own_runs()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        var at = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

        await store.RecordAsync(Run(unit: null, startedAt: at));

        Assert.Equal(at, await store.LastRunAtAsync(Task, null));
    }

    [Fact]
    public async Task One_unit_does_not_answer_for_another()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        await store.RecordAsync(Run("AniList"));

        Assert.Null(await store.LastRunAtAsync(Task, "MyAnimeList"));
        Assert.Null(await store.LastRunAtAsync("relations", "AniList"));
    }

    /// <summary>
    /// Every run that happened moves the clock, whatever it did.
    /// </summary>
    /// <remarks>
    /// This is the whole reason due-ness is not read from <c>SyncRun</c>. That
    /// table records only runs that reached a terminal state, so a cancelled run left
    /// no trace and the next tick started it again — which would have made cancelling
    /// a button that does nothing. A failure counts for the same reason: nothing
    /// reschedules itself any more, so a failing task waits out its ordinary cadence
    /// rather than being retried immediately.
    /// </remarks>
    [Theory]
    [InlineData(JobOutcome.Succeeded)]
    [InlineData(JobOutcome.NothingToDo)]
    [InlineData(JobOutcome.Failed)]
    [InlineData(JobOutcome.Cancelled)]
    public async Task Any_run_that_happened_moves_the_clock(JobOutcome outcome)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        var at = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

        await store.RecordAsync(Run("AniList", outcome, at));

        Assert.Equal(at, await store.LastRunAtAsync(Task, "AniList"));
    }

    [Fact]
    public async Task History_is_bounded_and_keeps_the_newest()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        for (var i = 0; i < JobRunStore.Retained + 25; i++)
        {
            await store.RecordAsync(Run("AniList", startedAt: DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        await using var context = database.CreateContext();
        var kept = context.JobRuns.Where(r => r.UnitKey == "AniList").ToList();

        Assert.Equal(JobRunStore.Retained, kept.Count);

        // The newest are what survived: the oldest kept is the twenty-sixth written.
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(25), kept.Min(r => r.StartedAt));
    }

    /// <summary>
    /// A busy unit cannot crowd out a quiet one.
    /// </summary>
    /// <remarks>
    /// Pruning per unit rather than per task, so a source syncing hourly does not
    /// erase the history of one syncing weekly — which is exactly the history somebody
    /// would go looking for, because it is the one they cannot remember.
    /// </remarks>
    [Fact]
    public async Task Pruning_one_unit_leaves_another_alone()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new JobRunStore(database.ContextFactory);

        await store.RecordAsync(Run("MyAnimeList"));

        for (var i = 0; i < JobRunStore.Retained + 25; i++)
        {
            await store.RecordAsync(Run("AniList"));
        }

        await using var context = database.CreateContext();

        Assert.Equal(JobRunStore.Retained, context.JobRuns.Count(r => r.UnitKey == "AniList"));
        Assert.Equal(1, context.JobRuns.Count(r => r.UnitKey == "MyAnimeList"));
    }
}

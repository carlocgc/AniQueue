using AniQueue.Core.Jobs;
using AniQueue.Infrastructure.Jobs;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The rendezvous between the page and the runners: what it lists, what it queues,
/// and what a cancel does.
/// </summary>
public class TaskRegistryTests
{
    private static TaskRegistry Registered()
    {
        var registry = new TaskRegistry();

        registry.Register("sync", [new JobUnit("AniList", "AniList")]);
        registry.Register("relations", [new JobUnit(null, "Related titles")]);

        return registry;
    }

    [Fact]
    public void A_registered_task_has_a_row_before_it_has_ever_run()
    {
        // The state a fresh install is in, and the one the page is most needed in
        // (D27). A page listing only tasks that had already done something would be
        // empty exactly when somebody is trying to work out what AniQueue does.
        var rows = Registered().Snapshot();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsRunning));
    }

    /// <summary>
    /// Rows come out in the same order whatever order the runners started in.
    /// </summary>
    /// <remarks>
    /// Registration order was the first attempt and is not stable: each job has its
    /// own hosted service and they start concurrently, so rows moved on almost every
    /// boot. Seen for real on the tasks page before this was sorted.
    /// </remarks>
    [Fact]
    public void Rows_do_not_depend_on_which_runner_started_first()
    {
        var one = new TaskRegistry();
        one.Register("sync", [new JobUnit("AniList", "AniList")]);
        one.Register("relations", [new JobUnit(null, "Related titles")]);

        var other = new TaskRegistry();
        other.Register("relations", [new JobUnit(null, "Related titles")]);
        other.Register("sync", [new JobUnit("AniList", "AniList")]);

        Assert.Equal(
            one.Snapshot().Select(r => r.TaskKey),
            other.Snapshot().Select(r => r.TaskKey));
    }

    [Fact]
    public async Task A_request_reaches_the_runner_that_owns_the_task()
    {
        var registry = Registered();

        Assert.True(registry.RequestRun("sync", "AniList"));

        Assert.Equal("AniList", await registry.WaitForRequestAsync("sync", CancellationToken.None));
    }

    [Fact]
    public void A_request_for_something_unregistered_is_refused()
    {
        var registry = Registered();

        Assert.False(registry.RequestRun("artwork", null));
        Assert.False(registry.RequestRun("sync", "MyAnimeList"));
    }

    /// <summary>
    /// Pressing twice before the runner picks it up asks for one run, not two.
    /// </summary>
    /// <remarks>
    /// The same intent expressed twice. Without this a double-click queues a second
    /// run that starts the moment the first finishes, which on a scoring sweep is an
    /// hour of somebody's GPU they did not ask for.
    /// </remarks>
    [Fact]
    public async Task Two_presses_before_a_run_starts_are_one_request()
    {
        var registry = Registered();

        registry.RequestRun("relations", null);
        registry.RequestRun("relations", null);

        Assert.Null(await registry.WaitForRequestAsync("relations", CancellationToken.None));

        using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.WaitForRequestAsync("relations", idle.Token));
    }

    /// <summary>
    /// A request made while a run is in flight is a fresh one and gets its own run.
    /// </summary>
    /// <remarks>
    /// The other half of the coalescing rule. Collapsing this one too would mean a
    /// press during a run was silently discarded, which is worse than running twice:
    /// the user watched the button do nothing.
    /// </remarks>
    [Fact]
    public async Task A_press_during_a_run_is_a_new_request()
    {
        var registry = Registered();

        registry.RequestRun("relations", null);
        await registry.WaitForRequestAsync("relations", CancellationToken.None);

        using (registry.BeginRun("relations", null, CancellationToken.None))
        {
            registry.RequestRun("relations", null);
        }

        Assert.Null(await registry.WaitForRequestAsync("relations", CancellationToken.None));
    }

    [Fact]
    public void A_run_in_flight_shows_as_running_and_stops_when_it_ends()
    {
        var registry = Registered();

        using (registry.BeginRun("sync", "AniList", CancellationToken.None))
        {
            var running = registry.Snapshot().Single(r => r.TaskKey == "sync");

            Assert.True(running.IsRunning);
            Assert.NotNull(running.StartedAt);
        }

        Assert.False(registry.Snapshot().Single(r => r.TaskKey == "sync").IsRunning);
    }

    /// <summary>
    /// Cancelling trips the run's token and says it was a cancellation.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The token is how the work stops; <c>WasCancelled</c> is how
    /// the runner tells a cancel apart from a shutdown, which arrives the same way and
    /// must not be recorded as a run somebody stopped (D40).
    /// </remarks>
    [Fact]
    public void Cancelling_trips_the_token_and_is_distinguishable_from_shutdown()
    {
        var registry = Registered();

        using var run = registry.BeginRun("sync", "AniList", CancellationToken.None);

        Assert.False(run.Token.IsCancellationRequested);
        Assert.False(run.WasCancelled);

        Assert.True(registry.Cancel("sync", "AniList"));

        Assert.True(run.Token.IsCancellationRequested);
        Assert.True(run.WasCancelled);
    }

    [Fact]
    public void Shutdown_stops_a_run_without_looking_like_a_cancel()
    {
        var registry = Registered();

        using var stopping = new CancellationTokenSource();
        using var run = registry.BeginRun("sync", "AniList", stopping.Token);

        stopping.Cancel();

        Assert.True(run.Token.IsCancellationRequested);
        Assert.False(run.WasCancelled);
    }

    [Fact]
    public void Cancelling_something_that_is_not_running_does_nothing()
    {
        var registry = Registered();

        Assert.False(registry.Cancel("sync", "AniList"));
        Assert.False(registry.Cancel("artwork", null));
    }

    [Fact]
    public void The_page_is_told_when_a_run_starts_and_finishes()
    {
        var registry = Registered();
        var changes = 0;

        registry.Changed += () => changes++;

        using (registry.BeginRun("sync", "AniList", CancellationToken.None))
        {
            Assert.Equal(1, changes);
        }

        Assert.Equal(2, changes);
    }
}

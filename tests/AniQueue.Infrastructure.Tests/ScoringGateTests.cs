using AniQueue.Infrastructure.Recommendations;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// One model, one request at a time, and the waiting person counted.
/// </summary>
public class ScoringGateTests
{
    [Fact]
    public async Task Only_one_run_holds_the_model_at_a_time()
    {
        using var gate = new ScoringGate();

        var first = await gate.EnterSweepAsync();
        var second = gate.EnterInteractiveAsync();

        Assert.False(second.IsCompleted);

        first.Dispose();

        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Somebody_queued_behind_a_batch_is_counted_as_waiting()
    {
        // The flag the sweep reads between batches. It has to be true while the person
        // is still queued — once they have the model there is nothing left to stand
        // down for, which is precisely when it stops mattering.
        using var gate = new ScoringGate();

        Assert.False(gate.IsInteractiveWaiting);

        var batch = await gate.EnterSweepAsync();
        var person = gate.EnterInteractiveAsync();

        Assert.True(gate.IsInteractiveWaiting);

        batch.Dispose();
        var claim = await person.WaitAsync(TimeSpan.FromSeconds(5));

        // No longer waiting: they have it.
        Assert.False(gate.IsInteractiveWaiting);

        claim.Dispose();
    }

    [Fact]
    public async Task A_sweep_alone_leaves_nobody_waiting()
    {
        using var gate = new ScoringGate();

        using var batch = await gate.EnterSweepAsync();

        Assert.False(gate.IsInteractiveWaiting);
    }

    [Fact]
    public async Task Releasing_twice_does_not_let_two_runs_through()
    {
        // The failure this type exists to prevent, arriving through the type itself: a
        // second release would raise the count above one and admit two requests to a
        // server that answers one at a time.
        using var gate = new ScoringGate();

        var claim = await gate.EnterSweepAsync();

        claim.Dispose();
        claim.Dispose();

        using var first = await gate.EnterSweepAsync();
        var second = gate.EnterSweepAsync();

        Assert.False(second.IsCompleted);
    }

    [Fact]
    public async Task Giving_up_while_queued_stops_counting_as_waiting()
    {
        // Somebody who navigated away or pressed Stop. Left counted, the sweep would
        // stand down for a person who is no longer there — every tick, for as long as
        // the process lived.
        using var gate = new ScoringGate();
        using var cancellation = new CancellationTokenSource();

        using var batch = await gate.EnterSweepAsync();

        var person = gate.EnterInteractiveAsync(cancellation.Token);
        Assert.True(gate.IsInteractiveWaiting);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => person);
        Assert.False(gate.IsInteractiveWaiting);
    }
}

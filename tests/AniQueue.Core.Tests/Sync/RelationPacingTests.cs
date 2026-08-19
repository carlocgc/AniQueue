using AniQueue.Core.Sync;

namespace AniQueue.Core.Tests.Sync;

/// <summary>
/// Pacing, tested as arithmetic and without a clock — the same reason §8 gives for
/// testing the sync schedule that way. Pacing that needed real time to test would
/// be pacing nobody could test, and a test suite that sleeps to prove it waits is
/// a suite people stop running.
/// </summary>
public class RelationPacingTests
{
    [Fact]
    public void An_ordinary_response_is_spread_at_the_measured_limit()
    {
        // 30 requests a minute, measured, against 90 documented.
        Assert.Equal(TimeSpan.FromSeconds(2), RelationPacing.DelayBefore(remaining: 40));
    }

    [Fact]
    public void A_missing_header_is_treated_as_no_information_rather_than_as_empty()
    {
        // The header is not guaranteed. Waiting a minute between every request
        // because a proxy stripped it would turn a half-minute backfill into a
        // twelve-hour one.
        Assert.Equal(TimeSpan.FromSeconds(2), RelationPacing.DelayBefore(remaining: null));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    [InlineData(0)]
    public void A_nearly_spent_budget_waits_for_the_window_to_roll_over(int remaining)
    {
        Assert.Equal(TimeSpan.FromSeconds(60), RelationPacing.DelayBefore(remaining));
    }

    [Fact]
    public void The_threshold_leaves_room_for_something_else_spending_the_same_budget()
    {
        // Six is still ordinary pacing, five is not: a user pressing Sync Now draws
        // from the same pool, so the backfill stops before the last request rather
        // than at it.
        Assert.Equal(TimeSpan.FromSeconds(2), RelationPacing.DelayBefore(remaining: 6));
        Assert.Equal(TimeSpan.FromSeconds(60), RelationPacing.DelayBefore(remaining: 5));
    }

    [Fact]
    public void A_server_stating_a_wait_wins_over_anything_inferred()
    {
        // It is the server naming a number rather than this application guessing
        // one, and a 429 is the case where guessing shorter is actively harmful.
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            RelationPacing.DelayBefore(remaining: 40, retryAfter: TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_retry_after_shorter_than_the_ordinary_spacing_does_not_become_a_busy_loop(int seconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            RelationPacing.DelayBefore(remaining: null, retryAfter: TimeSpan.FromSeconds(seconds)));
    }
}

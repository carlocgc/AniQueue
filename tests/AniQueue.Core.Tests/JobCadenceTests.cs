using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;

namespace AniQueue.Core.Tests;

/// <summary>
/// The one comparison every task makes, now that they all make the same one.
/// </summary>
public class JobCadenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Off is the absence of a schedule rather than a very long one.
    /// </summary>
    /// <remarks>
    /// A task with no schedule still runs when the library changes and whenever
    /// somebody asks, which is what the settings page means by stopping the clock and
    /// nothing else. Neither of those reaches here.
    /// </remarks>
    [Fact]
    public void A_task_with_no_schedule_is_never_due_on_the_timer()
    {
        Assert.False(JobCadence.IsDue(SyncSchedule.Off, lastRun: null, Now));
        Assert.False(JobCadence.IsDue(SyncSchedule.Off, Now.AddYears(-1), Now));
    }

    [Fact]
    public void A_task_that_has_never_run_is_due()
    {
        Assert.True(JobCadence.IsDue(SyncSchedule.Daily, lastRun: null, Now));
    }

    [Fact]
    public void A_task_that_ran_within_the_interval_is_not_due()
    {
        Assert.False(JobCadence.IsDue(SyncSchedule.Hourly, Now.AddMinutes(-59), Now));
    }

    /// <summary>
    /// Exactly one interval later counts as due.
    /// </summary>
    /// <remarks>
    /// The runner ticks on its own period and a task is asked whenever that comes
    /// round, so a strict comparison would push an hourly task to the tick after the
    /// one it was ready for — an hour becoming an hour and a quarter, every time.
    /// </remarks>
    [Fact]
    public void A_task_that_ran_exactly_one_interval_ago_is_due()
    {
        Assert.True(JobCadence.IsDue(SyncSchedule.Hourly, Now.AddHours(-1), Now));
    }

    [Fact]
    public void A_task_that_ran_longer_ago_than_the_interval_is_due()
    {
        Assert.True(JobCadence.IsDue(SyncSchedule.Weekly, Now.AddDays(-8), Now));
    }
}

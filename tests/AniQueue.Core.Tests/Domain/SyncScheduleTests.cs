using AniQueue.Core.Domain;

namespace AniQueue.Core.Tests.Domain;

/// <summary>
/// The schedule values and what they mean in time.
///
/// Worth pinning despite being four lines of switch: these are stored as integers
/// and are a database contract, so a value quietly changing meaning would
/// reschedule every installation that had already chosen it — and the one that
/// matters most is <see cref="SyncSchedule.Off"/>, whose whole job is to be
/// distinguishable from "a very long interval" rather than folded into one.
/// </summary>
public class SyncScheduleTests
{
    [Fact]
    public void Ten_minutes_is_ten_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), SyncSchedule.TenMinutes.ToInterval());

    [Theory]
    [InlineData(SyncSchedule.Hourly, 1)]
    [InlineData(SyncSchedule.EverySixHours, 6)]
    [InlineData(SyncSchedule.Daily, 24)]
    [InlineData(SyncSchedule.Weekly, 24 * 7)]
    public void A_schedule_is_an_interval(SyncSchedule schedule, int expectedHours) =>
        Assert.Equal(TimeSpan.FromHours(expectedHours), schedule.ToInterval());

    [Fact]
    public void Off_is_not_an_interval_at_all() =>
        Assert.Null(SyncSchedule.Off.ToInterval());

    [Fact]
    public void The_stored_values_are_the_ones_already_written()
    {
        // Append only. Renumbering these silently changes what every existing row
        // means, which for this enum is how often somebody's server talks to
        // somebody else's API.
        Assert.Equal(0, (int)SyncSchedule.Off);
        Assert.Equal(1, (int)SyncSchedule.Hourly);
        Assert.Equal(2, (int)SyncSchedule.EverySixHours);
        Assert.Equal(3, (int)SyncSchedule.Daily);
        Assert.Equal(4, (int)SyncSchedule.Weekly);

        // Five rather than the one its position suggests. TenMinutes reads between Off
        // and Hourly because that is the order somebody chooses from, and is numbered
        // last because that is the order the values were written in. Asserting it here
        // is what stops a later tidy-up from making the two agree.
        Assert.Equal(5, (int)SyncSchedule.TenMinutes);
    }
}

using AniQueue.Core.Sync;

namespace AniQueue.Core.Tests;

/// <summary>
/// The guard that decides whether an automatic deletion happens at all.
///
/// Both halves of it are load-bearing and pull in opposite directions: the
/// proportion is what stops a whole library going, and the floor is what stops a
/// small one being unable to lose a single title.
/// </summary>
public class AbsenceRemovalCapTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(30, 5)]
    [InlineData(50, 5)]
    [InlineData(500, 50)]
    [InlineData(1200, 120)]
    public void The_cap_is_a_tenth_of_the_library_or_five_whichever_is_larger(int tracked, int expected) =>
        Assert.Equal(expected, AbsenceRemovalCap.For(tracked));

    [Fact]
    public void A_small_library_can_still_lose_a_title()
    {
        // A bare percentage would make this impossible: a tenth of eight is zero, so
        // every absence in a small library would be held forever and the setting
        // would never do anything.
        Assert.False(AbsenceRemovalCap.Exceeded(1, 8));
        Assert.False(AbsenceRemovalCap.Exceeded(5, 8));
        Assert.True(AbsenceRemovalCap.Exceeded(6, 8));
    }

    [Fact]
    public void A_large_library_losing_a_tenth_is_still_allowed_and_more_is_not()
    {
        Assert.False(AbsenceRemovalCap.Exceeded(50, 500));
        Assert.True(AbsenceRemovalCap.Exceeded(51, 500));
    }

    [Fact]
    public void A_library_that_lost_everything_is_always_over_the_cap()
    {
        // The reading this exists to refuse. A list gone private and a list emptied
        // on purpose are the same response from here.
        Assert.True(AbsenceRemovalCap.Exceeded(500, 500));
    }
}

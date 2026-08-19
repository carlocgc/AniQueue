using AniQueue.Core.Library;

namespace AniQueue.Core.Tests.Library;

public class RuntimeCalculatorTests
{
    [Theory]
    [InlineData(12, 24, 288)]   // the brief's worked example
    [InlineData(1, 90, 90)]
    [InlineData(26, 24, 624)]
    public void Estimates_episodes_times_duration(int episodes, int duration, int expected) =>
        Assert.Equal(expected, RuntimeCalculator.Estimate(episodes, duration));

    [Theory]
    [InlineData(null, 24)]
    [InlineData(12, null)]
    [InlineData(null, null)]
    [InlineData(0, 24)]     // an unknown count is written as 0 by MyAnimeList
    [InlineData(12, 0)]
    public void Refuses_to_estimate_without_both_numbers(int? episodes, int? duration)
    {
        // The brief is explicit: runtime is never invented. A missing value must
        // produce nothing, not a plausible-looking guess.
        Assert.Null(RuntimeCalculator.Estimate(episodes, duration));
    }

    [Theory]
    [InlineData(105, "1h 45m")]
    [InlineData(288, "4h 48m")]
    [InlineData(1320, "22h")]      // whole hours omit a redundant "0m"
    [InlineData(45, "45m")]
    [InlineData(60, "1h")]
    public void Formats_durations_for_people(int minutes, string expected) =>
        Assert.Equal(expected, RuntimeCalculator.Format(minutes));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Formats_nothing_when_there_is_nothing_to_say(int? minutes) =>
        Assert.Null(RuntimeCalculator.Format(minutes));

    [Fact]
    public void Sums_only_what_is_known()
    {
        var (total, partial) = RuntimeCalculator.Sum([100, null, 50]);

        Assert.Equal(150, total);

        // Flagged, because a total built from half a list's entries is misleading
        // unless the UI can say so.
        Assert.True(partial);
    }

    [Fact]
    public void A_complete_sum_is_not_flagged_as_partial()
    {
        var (total, partial) = RuntimeCalculator.Sum([100, 50]);

        Assert.Equal(150, total);
        Assert.False(partial);
    }

    [Fact]
    public void A_sum_of_nothing_knowable_is_null_rather_than_zero()
    {
        // Zero would read as "no time at all", which is a different claim from
        // "we cannot say".
        var (total, partial) = RuntimeCalculator.Sum([null, null]);

        Assert.Null(total);
        Assert.True(partial);
    }
}

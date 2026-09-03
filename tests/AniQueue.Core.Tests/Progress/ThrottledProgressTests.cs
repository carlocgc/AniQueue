using AniQueue.Core.Progress;

namespace AniQueue.Core.Tests.Progress;

public class ThrottledProgressTests
{
    private sealed class Recorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Reports { get; } = [];

        public void Report(OperationProgress value) => Reports.Add(value);
    }

    [Fact]
    public void Rapid_updates_within_one_stage_are_collapsed()
    {
        // Each report becomes a render batch on the Blazor circuit. Reporting once
        // per item on a 750-entry import would flood it and slow the very work the
        // dialog is describing.
        var recorder = new Recorder();
        var throttled = new ThrottledProgress(recorder, TimeSpan.FromSeconds(30));

        for (var i = 1; i <= 500; i++)
        {
            throttled.Report(new OperationProgress("Saving your library", i, 1000));
        }

        Assert.Single(recorder.Reports);
    }

    [Fact]
    public void A_new_stage_always_gets_through()
    {
        // Throttling must never cost the user a step; only repeats of one.
        var recorder = new Recorder();
        var throttled = new ThrottledProgress(recorder, TimeSpan.FromSeconds(30));

        throttled.Report(new OperationProgress("Reading the file"));
        throttled.Report(new OperationProgress("Reading the file"));
        throttled.Report(new OperationProgress("Comparing against your library"));
        throttled.Report(new OperationProgress("Saving your library"));

        Assert.Equal(
            ["Reading the file", "Comparing against your library", "Saving your library"],
            recorder.Reports.Select(r => r.Message));
    }

    [Fact]
    public void The_final_count_always_gets_through()
    {
        // Otherwise the dialog can finish reading "847 of 1,000" while the work is
        // done, which looks like it stalled.
        var recorder = new Recorder();
        var throttled = new ThrottledProgress(recorder, TimeSpan.FromSeconds(30));

        for (var i = 1; i <= 100; i++)
        {
            throttled.Report(new OperationProgress("Saving your library", i, 100));
        }

        var last = recorder.Reports[^1];
        Assert.Equal(100, last.Current);
        Assert.Equal(100, last.Total);
    }

    [Theory]
    [InlineData(0, 100, 0.0)]
    [InlineData(50, 100, 0.5)]
    [InlineData(100, 100, 1.0)]
    public void Fraction_reflects_the_counts(int current, int total, double expected) =>
        Assert.Equal(expected, new OperationProgress("x", current, total).Fraction);

    [Fact]
    public void Fraction_is_null_before_the_size_of_the_work_is_known()
    {
        // Normal at the start of an operation; the dialog shows an indeterminate
        // spinner rather than a bar stuck at zero.
        Assert.Null(new OperationProgress("Reading the file").Fraction);
        Assert.False(new OperationProgress("Reading the file").HasCount);
    }

    [Fact]
    public void A_count_beyond_the_total_cannot_report_more_than_complete()
    {
        Assert.Equal(1.0, new OperationProgress("x", 150, 100).Fraction);
    }
}

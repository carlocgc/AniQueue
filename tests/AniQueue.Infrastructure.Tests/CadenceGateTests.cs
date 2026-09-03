using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Artwork;
using AniQueue.Infrastructure.Jobs;
using AniQueue.Infrastructure.Library;
using AniQueue.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The two tasks that used to ignore the cadence entirely, and now do not.
/// </summary>
/// <remarks>
/// Both gate on their own precondition rather than on a schedule, which is what lets
/// them converge and then do nothing. That is still true of the <i>work</i>; what
/// changed is when they are allowed to look. A page saying "once a day" and a row
/// moving every quarter of an hour is a setting the application is not keeping.
/// </remarks>
public class CadenceGateTests
{
    /// <summary>Counts the passes it was asked to make, and finds nothing to do.</summary>
    private sealed class CountingArtwork : IArtworkService
    {
        public int Passes { get; private set; }

        public Task<ArtworkPassResult> RunAsync(TimeSpan budget, CancellationToken cancellationToken)
        {
            Passes++;
            return Task.FromResult(new ArtworkPassResult());
        }
    }

    /// <summary>The same, for relations.</summary>
    private sealed class CountingBackfill : IRelationBackfill
    {
        public int Passes { get; private set; }

        public Task<RelationBackfillResult> RunAsync(
            TimeSpan budget,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Passes++;
            return Task.FromResult(RelationBackfillResult.Idle);
        }

        public Task<int> ForgetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<RelationCoverage> GetCoverageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RelationCoverage.None);
    }

    private static (CoverArtJob Job, CountingArtwork Artwork, FakeJobRunStore Runs) CoverArt(
        SyncSchedule cadence,
        DateTimeOffset? lastRun)
    {
        var artwork = new CountingArtwork();
        var runs = new FakeJobRunStore { LastRunAt = lastRun };

        return (
            new CoverArtJob(
                artwork,
                new LibraryChangeNotifier(NullLogger<LibraryChangeNotifier>.Instance),
                runs,
                new StaticOptionsMonitor<TaskOptions>(new TaskOptions { Schedule = cadence }),
                NullLogger<CoverArtJob>.Instance),
            artwork,
            runs);
    }

    private static (RelationBackfillJob Job, CountingBackfill Backfill) Relations(
        SyncSchedule cadence,
        DateTimeOffset? lastRun)
    {
        var backfill = new CountingBackfill();

        return (
            new RelationBackfillJob(
                backfill,
                new LibraryChangeNotifier(NullLogger<LibraryChangeNotifier>.Instance),
                new FakeJobRunStore { LastRunAt = lastRun },
                new StaticOptionsMonitor<TaskOptions>(new TaskOptions { Schedule = cadence }),
                NullLogger<RelationBackfillJob>.Instance),
            backfill);
    }

    [Theory]
    [InlineData(SyncSchedule.Off)]
    [InlineData(SyncSchedule.Daily)]
    public async Task Cover_art_does_not_look_on_a_tick_before_the_cadence_has_come_round(
        SyncSchedule cadence)
    {
        var (job, artwork, _) = CoverArt(cadence, DateTimeOffset.UtcNow.AddMinutes(-20));

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(JobOutcome.NotDue, outcome.Outcome);
        Assert.Equal(0, artwork.Passes);
    }

    [Fact]
    public async Task Cover_art_looks_on_a_tick_once_the_cadence_has_come_round()
    {
        var (job, artwork, _) = CoverArt(SyncSchedule.Hourly, DateTimeOffset.UtcNow.AddHours(-2));

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(JobOutcome.NothingToDo, outcome.Outcome);
        Assert.Equal(1, artwork.Passes);
    }

    /// <summary>
    /// A library change still brings it forward, whatever the cadence says.
    /// </summary>
    /// <remarks>
    /// This is what stops a title synced at nine o'clock waiting until tomorrow for
    /// its picture, and it is the reason the cadence gates the timer rather than the
    /// job.
    /// </remarks>
    [Theory]
    [InlineData(JobTrigger.LibraryChange)]
    [InlineData(JobTrigger.Manual)]
    public async Task Cover_art_still_answers_a_change_and_a_button_with_the_clock_stopped(
        JobTrigger trigger)
    {
        var (job, artwork, _) = CoverArt(SyncSchedule.Off, DateTimeOffset.UtcNow);

        var outcome = await job.RunAsync(new JobRunContext(trigger), CancellationToken.None);

        Assert.Equal(JobOutcome.NothingToDo, outcome.Outcome);
        Assert.Equal(1, artwork.Passes);
    }

    [Theory]
    [InlineData(SyncSchedule.Off)]
    [InlineData(SyncSchedule.Daily)]
    public async Task Related_titles_do_not_look_on_a_tick_before_the_cadence_has_come_round(
        SyncSchedule cadence)
    {
        var (job, backfill) = Relations(cadence, DateTimeOffset.UtcNow.AddMinutes(-20));

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(JobOutcome.NotDue, outcome.Outcome);
        Assert.Equal(0, backfill.Passes);
    }

    [Fact]
    public async Task Related_titles_look_on_a_tick_once_the_cadence_has_come_round()
    {
        var (job, backfill) = Relations(SyncSchedule.Hourly, DateTimeOffset.UtcNow.AddHours(-2));

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(JobOutcome.NothingToDo, outcome.Outcome);
        Assert.Equal(1, backfill.Passes);
    }

    [Theory]
    [InlineData(JobTrigger.LibraryChange)]
    [InlineData(JobTrigger.Manual)]
    public async Task Related_titles_still_answer_a_change_and_a_button_with_the_clock_stopped(
        JobTrigger trigger)
    {
        var (job, backfill) = Relations(SyncSchedule.Off, DateTimeOffset.UtcNow);

        var outcome = await job.RunAsync(new JobRunContext(trigger), CancellationToken.None);

        Assert.Equal(JobOutcome.NothingToDo, outcome.Outcome);
        Assert.Equal(1, backfill.Passes);
    }

    /// <summary>
    /// A switched-off task is refused before the cadence is even consulted.
    /// </summary>
    /// <remarks>
    /// The order matters for what the log says: "switched off" is a different problem
    /// from "not due yet", and the first is the one somebody is looking for when a
    /// task appears to do nothing.
    /// </remarks>
    [Fact]
    public async Task A_switched_off_task_is_refused_whatever_the_trigger()
    {
        var backfill = new CountingBackfill();

        var job = new RelationBackfillJob(
            backfill,
            new LibraryChangeNotifier(NullLogger<LibraryChangeNotifier>.Instance),
            new FakeJobRunStore(),
            new StaticOptionsMonitor<TaskOptions>(
                new TaskOptions { Schedule = SyncSchedule.Hourly, RelationsEnabled = false }),
            NullLogger<RelationBackfillJob>.Instance);

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Manual), CancellationToken.None);

        Assert.Equal(JobOutcome.NotDue, outcome.Outcome);
        Assert.Equal(0, backfill.Passes);
    }
}

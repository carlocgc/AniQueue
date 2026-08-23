using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Import;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;
using AniQueue.Core.Settings;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// When an unattended run happens, which is the job's whole responsibility — the
/// service decides what a run does, and is stubbed out here entirely.
///
/// No database and no network: everything under test is arithmetic over a status
/// the service reports, which is deliberate. Scheduling that needed a real clock
/// to test would be scheduling nobody could test.
/// </summary>
public class UnattendedSyncJobTests
{
    /// <summary>A sync service that records what it was asked to do, and does none of it.</summary>
    private sealed class StubSyncService(SourceSyncStatus status) : ISyncService
    {
        public List<AnimeSource> Ran { get; } = [];

        public UnattendedSyncResult Result { get; set; } =
            new() { Source = AnimeSource.AniList, Outcome = SyncOutcome.NothingToDo };

        public Task<IReadOnlyList<SourceSyncStatus>> GetStatusAsync(
            int profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceSyncStatus>>([status]);

        public Task<UnattendedSyncResult> RunUnattendedAsync(
            int profileId, AnimeSource source, CancellationToken cancellationToken = default)
        {
            Ran.Add(source);
            return Task.FromResult(Result);
        }

        public Task<SyncFetchResult> FetchAsync(
            int profileId,
            AnimeSource source,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SyncApplyResult> ApplyAsync(
            SyncFetchResult fetch,
            int profileId,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UserSettingsSaveResult> SaveSettingsAsync(
            SourceSyncSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserSettingsSaveResult.Success(SettingsPath));

        public Task<UserSettingsSaveResult> SetPrimarySourceAsync(
            int profileId, AnimeSource source, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserSettingsSaveResult.Success(SettingsPath));

        /// <summary>Somewhere for a save result to point at. Never written to.</summary>
        private const string SettingsPath = "userconfig.json";

        public Task SavePreferredTitleLanguageAsync(
            int profileId, TitleLanguage language, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<TitleLanguage> GetPreferredTitleLanguageAsync(
            int profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TitleLanguage.Romaji);
    }

    private sealed class RecordingNotifier : ILibraryChangeNotifier
    {
        public List<LibraryChange?> Published { get; } = [];

        public event Action<LibraryChange?>? Changed;

        public void Publish(LibraryChange? change = null)
        {
            Published.Add(change);
            Changed?.Invoke(change);
        }
    }

    private static SourceSyncStatus Status(
        SyncSchedule schedule,
        TimeSpan? sinceLastRun = null,
        int consecutiveFailures = 0,
        bool isConfigured = true,
        bool isEnabled = true) =>
        new()
        {
            Source = AnimeSource.AniList,
            CanFetch = true,
            IsConfigured = isConfigured,
            Account = isConfigured ? "someone" : null,
            ConsecutiveFailures = consecutiveFailures,
            IsPrimary = false,
            Settings = new SourceSyncSettings
            {
                Source = AnimeSource.AniList,
                Schedule = schedule,
                IsEnabled = isEnabled
            },
            LastRun = sinceLastRun is { } elapsed
                ? new SyncRun { StartedAt = DateTimeOffset.UtcNow - elapsed }
                : null
        };

    private static async Task<(StubSyncService Sync, RecordingNotifier Notifier)> RunAsync(
        SourceSyncStatus status,
        UnattendedSyncResult? result = null,
        JobTrigger trigger = JobTrigger.Timer)
    {
        var sync = new StubSyncService(status);
        var notifier = new RecordingNotifier();

        if (result is not null)
        {
            sync.Result = result;
        }

        var job = new UnattendedSyncJob(
            sync,
            notifier,

            // Due-ness comes from the run record since Phase 15b, so the elapsed time
            // a test wants to express is set here rather than on the status.
            new FakeJobRunStore
            {
                LastRunAt = status.LastRun is { } last ? last.StartedAt : null
            },
            NullLogger<UnattendedSyncJob>.Instance);
        await job.RunAsync(new JobRunContext(trigger, nameof(AnimeSource.AniList)), CancellationToken.None);

        return (sync, notifier);
    }

    [Fact]
    public async Task A_source_with_no_schedule_is_never_run()
    {
        // Off is the default, so this is what an installation that has configured an
        // account and nothing else does: exactly nothing, until asked.
        var (sync, _) = await RunAsync(Status(SyncSchedule.Off, sinceLastRun: TimeSpan.FromDays(30)));

        Assert.Empty(sync.Ran);
    }

    [Fact]
    public async Task A_scheduled_source_that_has_never_run_is_due_immediately()
    {
        var (sync, _) = await RunAsync(Status(SyncSchedule.Hourly));

        Assert.Equal(AnimeSource.AniList, Assert.Single(sync.Ran));
    }

    [Theory]
    [InlineData(SyncSchedule.Hourly, 10, false)]
    [InlineData(SyncSchedule.Hourly, 61, true)]
    [InlineData(SyncSchedule.EverySixHours, 300, false)]
    [InlineData(SyncSchedule.EverySixHours, 361, true)]
    [InlineData(SyncSchedule.Daily, 1_000, false)]
    [InlineData(SyncSchedule.Daily, 1_500, true)]
    [InlineData(SyncSchedule.Weekly, 10_000, false)]
    [InlineData(SyncSchedule.Weekly, 10_081, true)]
    public async Task A_source_runs_once_its_interval_has_passed(
        SyncSchedule schedule,
        int minutesSinceLastRun,
        bool expectedToRun)
    {
        var (sync, _) = await RunAsync(
            Status(schedule, TimeSpan.FromMinutes(minutesSinceLastRun)));

        Assert.Equal(expectedToRun ? 1 : 0, sync.Ran.Count);
    }

    [Fact]
    public async Task A_source_switched_off_for_this_profile_is_not_run()
    {
        var (sync, _) = await RunAsync(
            Status(SyncSchedule.Hourly, TimeSpan.FromDays(1), isEnabled: false));

        Assert.Empty(sync.Ran);
    }

    [Fact]
    public async Task A_source_with_no_account_is_not_run()
    {
        // There is nothing to fetch, and a run would only record a failure the user
        // can already see stated on the page.
        var (sync, _) = await RunAsync(
            Status(SyncSchedule.Hourly, TimeSpan.FromDays(1), isConfigured: false));

        Assert.Empty(sync.Ran);
    }

    /// <summary>
    /// A failing source is retried on the schedule it was given, however long it has
    /// been failing.
    /// </summary>
    /// <remarks>
    /// This asserted the opposite until D40. The interval used to double per
    /// consecutive failure to a cap of sixteen, reasoning that a rate limit or an
    /// unreadable account does not improve for being asked again on the dot — true,
    /// and outweighed. Asking again costs one request; not asking costs a schedule
    /// the user chose being rewritten invisibly by the application, which is
    /// indistinguishable from a broken schedule. The case that settled it is a model
    /// or a service reachable only for a few hours a day, where failing is the normal
    /// state rather than a fault.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(50)]
    public async Task A_failing_source_is_retried_on_its_ordinary_schedule(int consecutiveFailures)
    {
        var justUnder = await RunAsync(Status(
            SyncSchedule.Hourly, TimeSpan.FromMinutes(50), consecutiveFailures));

        Assert.Empty(justUnder.Sync.Ran);

        var justOver = await RunAsync(Status(
            SyncSchedule.Hourly, TimeSpan.FromMinutes(70), consecutiveFailures));

        Assert.Single(justOver.Sync.Ran);
    }

    /// <summary>
    /// Pressing the button ignores the schedule; new data arriving does not.
    /// </summary>
    /// <remarks>
    /// Sync is what <i>publishes</i> a library change, and every runner including its
    /// own hears the broadcast — so a sync that treated the signal as a reason to
    /// fetch would schedule its own next run, forever. The jobs that bypass their
    /// cadence on that signal are the ones consuming it (D41).
    /// </remarks>
    [Theory]
    [InlineData(JobTrigger.Timer, false)]
    [InlineData(JobTrigger.LibraryChange, false)]
    [InlineData(JobTrigger.Manual, true)]
    public async Task Only_a_manual_run_ignores_the_schedule(JobTrigger trigger, bool expectedToRun)
    {
        var (sync, _) = await RunAsync(
            Status(SyncSchedule.Hourly, TimeSpan.FromMinutes(10)),
            trigger: trigger);

        Assert.Equal(expectedToRun ? 1 : 0, sync.Ran.Count);
    }

    [Fact]
    public async Task A_run_that_changed_something_tells_open_pages()
    {
        var (_, notifier) = await RunAsync(
            Status(SyncSchedule.Hourly),
            new UnattendedSyncResult
            {
                Source = AnimeSource.AniList,
                Outcome = SyncOutcome.Succeeded,
                Created = 2,
                SlotsReleased = 1
            });

        // Not null: a sync is the one job with something a page can render, so it
        // always publishes a payload (D41).
        var change = Assert.IsType<LibraryChange>(Assert.Single(notifier.Published));
        Assert.Equal(2, change.Created);
        Assert.Equal(1, change.SlotsReleased);
    }

    [Fact]
    public async Task A_run_that_changed_nothing_says_nothing()
    {
        // A page that offers to refresh itself hourly for no reason teaches its user
        // to ignore the offer.
        var (_, notifier) = await RunAsync(
            Status(SyncSchedule.Hourly),
            new UnattendedSyncResult
            {
                Source = AnimeSource.AniList,
                Outcome = SyncOutcome.NothingToDo
            });

        Assert.Empty(notifier.Published);
    }

    [Fact]
    public async Task A_run_that_only_held_changes_says_nothing()
    {
        // Nothing was written, so nothing an open page shows has moved on. The count
        // is on the Sources page, which is where the user would go to act on it.
        var (_, notifier) = await RunAsync(
            Status(SyncSchedule.Hourly),
            new UnattendedSyncResult
            {
                Source = AnimeSource.AniList,
                Outcome = SyncOutcome.HeldForReview,
                ChangesHeld = 12
            });

        Assert.Empty(notifier.Published);
    }
}

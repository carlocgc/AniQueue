using AniQueue.Core.Domain;
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
        public List<LibraryChange> Published { get; } = [];

        public event Action<LibraryChange>? Changed;

        public void Publish(LibraryChange change)
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
        UnattendedSyncResult? result = null)
    {
        var sync = new StubSyncService(status);
        var notifier = new RecordingNotifier();

        if (result is not null)
        {
            sync.Result = result;
        }

        var job = new UnattendedSyncJob(sync, notifier, NullLogger<UnattendedSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

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

    [Theory]
    [InlineData(1, 90, false)]
    [InlineData(1, 130, true)]
    [InlineData(3, 400, false)]
    [InlineData(3, 500, true)]
    // Capped at sixteen hours, so a source that broke a month ago still notices
    // within a day of being fixed rather than waiting years.
    [InlineData(50, 900, false)]
    [InlineData(50, 1_000, true)]
    public async Task A_failing_source_is_retried_progressively_less_often(
        int consecutiveFailures,
        int minutesSinceLastRun,
        bool expectedToRun)
    {
        var (sync, _) = await RunAsync(Status(
            SyncSchedule.Hourly,
            TimeSpan.FromMinutes(minutesSinceLastRun),
            consecutiveFailures));

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

        var change = Assert.Single(notifier.Published);
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

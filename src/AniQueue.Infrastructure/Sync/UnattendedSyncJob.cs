using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Reads every source whose schedule says it is due, with nobody present.
///
/// The job owns <i>when</i>; <see cref="ISyncService"/> owns <i>what</i>. That
/// split is what keeps the runner generic: scheduling here is a per-source user
/// setting that can change while the application is running, so it is answered
/// from the database on each tick rather than baked into a timer at startup.
/// </summary>
public sealed class UnattendedSyncJob(
    ISyncService syncService,
    ILibraryChangeNotifier notifier,
    ILogger<UnattendedSyncJob> logger) : IBackgroundJob
{
    /// <summary>
    /// The shortest schedule offered is an hour, so asking every five minutes is
    /// enough to honour one and costs two indexed reads.
    /// </summary>
    /// <remarks>
    /// It also bounds how late a run can be, and that lateness does not accumulate:
    /// due-ness is measured from when the last run <i>started</i>, so an hourly
    /// schedule runs at 60–65 minutes each time rather than drifting further out
    /// with every tick.
    /// </remarks>
    public TimeSpan TickPeriod => TimeSpan.FromMinutes(5);

    public string Name => "Unattended sync";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var statuses = await syncService.GetStatusAsync(Profile.DefaultProfileId, cancellationToken);

        foreach (var status in statuses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDue(status, DateTimeOffset.UtcNow))
            {
                continue;
            }

            var result = await syncService.RunUnattendedAsync(
                Profile.DefaultProfileId, status.Source, cancellationToken);

            if (result.Outcome is { } outcome)
            {
                logger.LogInformation(
                    "Unattended sync for {Source} finished as {Outcome}",
                    status.Source,
                    outcome);
            }

            // Only when something actually moved. A poll that found nothing is not
            // news, and a page that offers to refresh itself hourly for no reason
            // teaches its user to ignore the offer.
            if (result.ChangedLibrary)
            {
                notifier.Publish(new LibraryChange
                {
                    Source = result.Source,
                    Created = result.Created,
                    Updated = result.Updated,
                    SlotsReleased = result.SlotsReleased,
                    AbsentFlagged = result.AbsentFlagged
                });
            }
        }
    }

    /// <summary>
    /// Whether enough time has passed since this source last ran.
    /// </summary>
    /// <remarks>
    /// Measured from the last run's start rather than its finish, so a slow fetch
    /// does not push the schedule out by its own duration.
    ///
    /// A source that has never run is due immediately — which is deliberate, and
    /// safe, because turning the schedule on is what creates that state and the
    /// user has just said they want it read.
    /// </remarks>
    private static bool IsDue(SourceSyncStatus status, DateTimeOffset now)
    {
        if (!status.IsConfigured || !status.Settings.IsEnabled)
        {
            return false;
        }

        if (status.Settings.Schedule.ToInterval() is not { } interval)
        {
            return false;
        }

        if (status.LastRun is not { } last)
        {
            return true;
        }

        return now - last.StartedAt >= interval * BackoffMultiplier(status.ConsecutiveFailures);
    }

    /// <summary>
    /// Stretches the interval while a source keeps failing.
    /// </summary>
    /// <remarks>
    /// Doubling per failure, capped at sixteen times the configured interval. What
    /// this is protecting is the far end: the failures worth backing off from are a
    /// rate limit, an outage, or an account that cannot be read at all, and none of
    /// them improve for being asked again on the dot. The cap stops a source that
    /// broke a month ago from waiting years to notice it is fixed, and Sync Now
    /// ignores all of this — a user who has fixed the account should not have to
    /// wait out a backoff they cannot see.
    ///
    /// Derived from the run record rather than held in memory, so a restart neither
    /// forgets the backoff nor resets it — and a restart is exactly what somebody
    /// does after editing the account in a settings file.
    /// </remarks>
    private static int BackoffMultiplier(int consecutiveFailures) =>
        consecutiveFailures <= 0 ? 1 : 1 << Math.Min(consecutiveFailures, 4);
}

using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Reads one source whose schedule says it is due, with nobody present.
///
/// The job owns <i>when</i>; <see cref="ISyncService"/> owns <i>what</i>. That
/// split is what keeps the runner generic: scheduling here is a user setting that
/// can change while the application is running, so it is answered on each tick
/// rather than baked into a timer at startup.
/// </summary>
/// <remarks>
/// <b>One unit per fetchable source</b> (D40), which is why this no longer loops.
/// Each source carries its own enabled state and its own failure history, so one row
/// covering both would have to aggregate two of everything — and the runner asking
/// once per unit is what lets one be run, cancelled or switched off without touching
/// the other.
/// </remarks>
public sealed class UnattendedSyncJob(
    ISyncService syncService,
    ILibraryChangeNotifier notifier,
    IJobRunStore runs,
    IOptionsMonitor<TaskOptions> tasks,
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

    /// <summary>What this task's runs are filed under. Never shown.</summary>
    public string Key => "sync";

    public string Name => "Sync";

    /// <summary>
    /// One per source something can actually be fetched from.
    /// </summary>
    /// <remarks>
    /// A file source is never here. It has no list to go and read, so it can never be
    /// due, and a row whose button could never do anything is a worse answer than no
    /// row at all (D40).
    /// </remarks>
    public IReadOnlyList<JobUnit> Units { get; } =
        [new JobUnit(nameof(AnimeSource.AniList), "AniList")];

    public async Task<JobRunOutcome> RunAsync(
        JobRunContext context,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AnimeSource>(context.Unit, out var source))
        {
            // The runner only ever passes back a key this job published, so this is a
            // programming error rather than a user-facing failure.
            throw new ArgumentOutOfRangeException(
                nameof(context), context.Unit, "This job has no such unit.");
        }

        var statuses = await syncService.GetStatusAsync(Profile.DefaultProfileId, cancellationToken);

        if (statuses.FirstOrDefault(s => s.Source == source) is not { } status)
        {
            return JobRunOutcome.NotDue;
        }

        if (!await IsDueAsync(status, context, cancellationToken))
        {
            return JobRunOutcome.NotDue;
        }

        var result = await syncService.RunUnattendedAsync(
            Profile.DefaultProfileId, source, cancellationToken);

        if (result.Outcome is not { } outcome)
        {
            // Refused after the fact — the kill switch, or an account that went away
            // between the status read and the run.
            return JobRunOutcome.NotDue;
        }

        logger.LogInformation("Unattended sync for {Source} finished as {Outcome}", source, outcome);

        // Only when something actually moved. A poll that found nothing is not news,
        // and a page that offers to refresh itself hourly for no reason teaches its
        // user to ignore the offer.
        if (result.ChangedLibrary)
        {
            notifier.Publish(
                new LibraryChange
                {
                    Source = result.Source,
                    Created = result.Created,
                    Updated = result.Updated,
                    SlotsReleased = result.SlotsReleased,
                    AbsentFlagged = result.AbsentFlagged
                },
                Key);
        }

        var changed = result.Created + result.Updated + result.SlotsReleased + result.AbsentFlagged;

        return outcome switch
        {
            SyncOutcome.Failed => JobRunOutcome.Failed(
                result.FailureReason ?? "The sync did not finish."),

            SyncOutcome.NothingToDo => JobRunOutcome.NothingToDo,

            // Held changes count as processed and not as changed, which is the honest
            // reading: the run reached the source and found work, and deliberately did
            // not apply it (D21).
            SyncOutcome.HeldForReview => JobRunOutcome.Succeeded(
                result.ChangesHeld + result.ConflictsHeld, 0),

            _ => JobRunOutcome.Succeeded(changed, changed)
        };
    }

    /// <summary>
    /// Whether enough time has passed since this source last ran.
    /// </summary>
    /// <remarks>
    /// Measured from the last run's start rather than its finish, so a slow fetch
    /// does not push the schedule out by its own duration.
    ///
    /// <b>From <c>JobRun</c> rather than from <c>SyncRun</c>, since Phase 15b.</b>
    /// <c>SyncRun</c> is written only when a run reaches a terminal state, so a run
    /// that was cancelled — or that threw before it could record anything — left the
    /// clock unmoved and was started again on the very next tick. Reading it from the
    /// record every run writes is what makes cancelling mean "not this cycle" instead
    /// of "try again in five minutes". <c>SyncRun</c> goes back to being purely the
    /// library's audit trail, which is what its own documentation says it is.
    ///
    /// A source that has never run is due immediately — which is deliberate, and
    /// safe, because turning the schedule on is what creates that state and the
    /// user has just said they want it read.
    ///
    /// <b>A failing source is not made to wait longer.</b> Until D40 the interval was
    /// doubled per consecutive failure to a cap of sixteen, reasoning that a rate
    /// limit or an unreadable account does not improve for being asked again on the
    /// dot. True, and outweighed: asking again costs one request, while not asking
    /// costs a schedule the user set being rewritten by the application, invisibly,
    /// in response to a condition they may already know about.
    /// </remarks>
    private async Task<bool> IsDueAsync(
        SourceSyncStatus status,
        JobRunContext context,
        CancellationToken cancellationToken)
    {
        // Nothing to fetch is the first question. A file source has no list to go and
        // read, so it is never due — asking would be a programming error, not a
        // failed run.
        if (!status.CanFetch || !status.IsConfigured || !status.Settings.IsEnabled)
        {
            return false;
        }

        // Manual only, and deliberately not JobRunContext.IgnoresSchedule, which also
        // covers a library change. A sync is not something to start because data moved:
        // relations writing edges, or a file being imported, is no reason to go and
        // re-read somebody's list. This job is a producer of library changes; the jobs
        // that bypass their cadence on that signal are the ones consuming it (D41).
        //
        // It no longer needs to guard against its *own* announcements — the runner
        // discards those now — but the rule stands on its own without that.
        //
        // Pressing the button is a different thing entirely, and ignoring the schedule
        // is what Sync Now has always meant.
        if (context.Trigger is JobTrigger.Manual)
        {
            return true;
        }

        // One cadence for every background task since Phase 15c (D40). Read on each
        // tick rather than captured, so a change on the tasks page takes effect
        // without a restart.
        if (tasks.CurrentValue.Schedule.ToInterval() is not { } interval)
        {
            return false;
        }

        var lastRun = await runs.LastRunAtAsync(Key, context.Unit, cancellationToken);

        return lastRun is not { } last || DateTimeOffset.UtcNow - last >= interval;
    }
}

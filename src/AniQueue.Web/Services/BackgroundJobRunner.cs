using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;

namespace AniQueue.Web.Services;

/// <summary>
/// Drives one <see cref="IBackgroundJob"/> on a timer: tick, open a scope, run each
/// of its units, catch, log.
///
/// It lives in the web project rather than beside the job it runs because that is
/// where hosting lives — Infrastructure has no reference to
/// <c>Microsoft.Extensions.Hosting</c> and gains nothing by taking one to own a
/// loop that is host composition either way.
/// </summary>
/// <remarks>
/// <b>Ticks cannot overlap</b>, which is the property that matters most here and is
/// structural rather than guarded: the loop is sequential, so a run that outlasts
/// its tick period simply delays the next one. <see cref="PeriodicTimer"/> is what
/// makes that safe — it coalesces missed ticks into a single immediate one rather
/// than queueing every tick that passed, so a slow response cannot come back to a
/// backlog of syncs firing at once. One timeout turning a five-minute interval into
/// concurrent syncs racing each other is precisely the failure this shape prevents.
///
/// It is also what will make <i>Run now</i> safe when Phase 15c adds it: a manual
/// run enters this loop like any other rather than starting work beside it.
///
/// <b>A scope per tick</b>, because the job resolves scoped services that open
/// short-lived database contexts (D3). A scope held for the process lifetime would
/// accumulate tracked entities for as long as the application runs.
///
/// <b>Units run one at a time, in order.</b> The job is asked once per unit (D40),
/// which is what lets a row on the tasks page mean one source rather than all of
/// them — and what will let one be cancelled without stopping the rest.
///
/// <b>A library change also ticks it</b>, ahead of the timer. Without that, syncing
/// several hundred new titles left every one of them without relations until the
/// next quarter-hour came round, and the Sources page's refresh button looked like
/// the only way to get them — which is how an automatic job comes to be operated by
/// hand.
///
/// This is deliberately <i>not</i> the sync calling the backfill. Nothing here knows
/// which job it is running or what any other job does: the signal says data changed,
/// every runner hears it, and each one still gates on its own precondition and finds
/// nothing if it has nothing to do (D25, D41). A job woken with no work is a no-op,
/// which is what makes a shared signal safe to broadcast.
///
/// <b>Nothing reschedules itself.</b> A failing job used to have its interval
/// stretched to sixteen ticks, on the reasoning that a rate limit or an outage does
/// not improve for being asked again on the dot. D40 deletes that: what it cost was
/// a schedule the user chose being rewritten invisibly, and a model served from a
/// machine that is on for a few hours a day fails most of the time by design. A
/// failure is logged and the cadence decides when to try again.
/// </remarks>
public sealed class BackgroundJobRunner<TJob>(
    IServiceScopeFactory scopeFactory,
    ILibraryChangeNotifier changes,
    ITaskRegistry registry,
    ITaskRunnerBridge bridge,
    ILogger<BackgroundJobRunner<TJob>> logger) : BackgroundService
    where TJob : notnull, IBackgroundJob
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read before the wake-up handler is defined, because the handler closes over
        // the key to recognise this job's own announcements.
        TimeSpan period;
        string name;
        string key;

        using (var scope = scopeFactory.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<TJob>();
            period = job.TickPeriod;
            name = job.Name;
            key = job.Key;

            // Registered before the first tick, so every task has a row from startup
            // rather than from whenever it first happens to run. A page that only
            // listed tasks that had already done something would be least useful on a
            // fresh install, which is where it is most needed (D27).
            registry.Register(key, job.Units);
        }

        logger.LogInformation("{Job} will be checked every {Period}", name, period);

        // The first tick is one period away, not immediate. Startup is the busiest
        // the process ever is — migrations, seeding, the first render — and there is
        // nothing a job could do in that window that will not keep for five minutes.
        using var timer = new PeriodicTimer(period);

        // Capacity one, so a burst of changes is one wake-up rather than a queue of
        // them. A job that has just run has nothing to gain from running again
        // because two things changed while it did.
        using var woken = new SemaphoreSlim(0, 1);

        void OnLibraryChanged(LibraryChangeNotification notification)
        {
            // A job never wakes itself. Every job announces what it changed (D41), and
            // a job that changes something on most runs would otherwise wake its own
            // runner on most runs — one wasted pass and one history row per run saying
            // a task ran for no reason. Seen for real: a relation pass that wrote 826
            // edges produced a second run, triggered by its own news, that found
            // nothing.
            if (notification.Origin == key)
            {
                return;
            }

            try
            {
                woken.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending. That is the coalescing working.
            }
            catch (ObjectDisposedException)
            {
                // Shutting down; the loop below has already stopped waiting.
            }
        }

        changes.Changed += OnLibraryChanged;

        // Exactly one outstanding wait on each source, carried across iterations.
        // PeriodicTimer permits only one pending WaitForNextTickAsync at a time, so
        // the tick task is renewed only once it has actually completed.
        var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var wake = woken.WaitAsync(stoppingToken);

        // The third wake source, and the one that makes Run now safe: a request is
        // delivered into this loop rather than starting work beside it, so pressing
        // the button while a tick is in flight queues behind it instead of racing it.
        var asked = bridge.WaitForRequestAsync(key, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var ready = await Task.WhenAny(tick, wake, asked);

                if (ready == tick)
                {
                    if (!await tick)
                    {
                        break;
                    }

                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();

                    await RunUnitsAsync(key, name, JobTrigger.Timer, unit: null, stoppingToken);
                }
                else if (ready == wake)
                {
                    // Awaited rather than discarded so a cancellation surfaces here
                    // instead of as an unobserved faulted task.
                    await wake;
                    wake = woken.WaitAsync(stoppingToken);

                    await RunUnitsAsync(key, name, JobTrigger.LibraryChange, unit: null, stoppingToken);
                }
                else
                {
                    var requested = await asked;
                    asked = bridge.WaitForRequestAsync(key, stoppingToken);

                    // One unit, not all of them. A row's button means that row.
                    await RunUnitsAsync(key, name, JobTrigger.Manual, requested, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Not an error, and not worth a stack trace in the log of a
            // container that was asked to stop.
        }
        finally
        {
            // The notifier is a singleton and outlives nothing, so a handler left
            // subscribed here would be held for the life of the process.
            changes.Changed -= OnLibraryChanged;
        }

        logger.LogInformation("{Job} has stopped", name);
    }

    /// <summary>
    /// Asks the job about each of its units in turn.
    /// </summary>
    /// <remarks>
    /// One scope for the whole tick rather than one per unit: the units of a job
    /// share its services, and a sync reading two sources is one visit to the
    /// database's connection pool rather than two.
    ///
    /// <b>A failing unit does not stop the others.</b> Each is caught on its own,
    /// because they are independent by construction — that is what makes them
    /// separate rows — and one source with a private list must not stop another from
    /// being read.
    /// </remarks>
    private async Task RunUnitsAsync(
        string key,
        string name,
        JobTrigger trigger,
        string? unit,
        CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<TJob>();
        var runs = scope.ServiceProvider.GetRequiredService<IJobRunStore>();

        // A timer or a library change asks about everything; a button asks about the
        // row it is on.
        var due = unit is null
            ? job.Units
            : [.. job.Units.Where(u => u.Key == unit)];

        foreach (var current in due)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var context = new JobRunContext(trigger, current.Key);
            var startedAt = DateTimeOffset.UtcNow;

            // Held for the run, which is what puts the unit on the page as running and
            // gives Cancel something to trip.
            using var run = bridge.BeginRun(key, current.Key, stoppingToken);

            JobRunOutcome outcome;

            try
            {
                outcome = await job.RunAsync(context, run.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (run.WasCancelled)
            {
                // Somebody pressed stop. Not a failure, and recorded so that the
                // cadence clock moves — which is what makes cancelling mean "skip this
                // cycle" rather than "try again on the next tick" (D40).
                logger.LogInformation("{Job} / {Unit} was cancelled", name, current.Name);

                outcome = new JobRunOutcome(JobOutcome.Cancelled);
            }
            catch (Exception ex)
            {
                // Everything a job expects to go wrong it reports itself, as a failed
                // outcome. Reaching here means something unforeseen — so it is logged
                // with its stack, and recorded as a failure with a reason that is not
                // one, because §6 forbids a stack trace reaching a page.
                //
                // Caught at all because an escaping exception ends the hosted service
                // and by default takes the host down with it. A background job failing
                // must not stop the application serving the pages that still work.
                logger.LogError(ex, "{Job} threw while running {Unit}", name, current.Name);

                outcome = JobRunOutcome.Failed("Something went wrong. The log has the details.");
            }

            // Deliberately on the stopping token rather than the run's: a cancelled run
            // still has to be written down, and writing it with the token that was just
            // tripped would throw the record away.
            await RecordAsync(runs, key, name, current, context, startedAt, outcome, stoppingToken);
        }
    }

    /// <summary>
    /// Writes down what a run did, and says it in the log.
    /// </summary>
    /// <remarks>
    /// <b>Recording a run that threw is what allows the backoff to be gone.</b>
    /// Due-ness is measured from the last recorded run, so a job that fails before it
    /// reports anything would leave that clock unmoved and be asked again on the very
    /// next tick, forever. The old runner absorbed that by stretching its own
    /// interval; D40 deletes the stretching, so the row has to exist instead.
    ///
    /// <b>The store failing must not take the job down with it.</b> A run that
    /// happened and could not be written is worse remembered than not remembered at
    /// all — but it is not a reason to stop the loop, so it is logged and the next
    /// tick carries on.
    /// </remarks>
    private async Task RecordAsync(
        IJobRunStore runs,
        string key,
        string name,
        JobUnit unit,
        JobRunContext context,
        DateTimeOffset startedAt,
        JobRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!outcome.IsRecordable)
        {
            return;
        }

        logger.LogInformation(
            "{Job} / {Unit}: {Outcome}, {Processed} considered, {Changed} changed{Reason}",
            name,
            unit.Name,
            outcome.Outcome,
            outcome.ItemsProcessed,
            outcome.ItemsChanged,
            outcome.FailureReason is { } reason ? $" — {reason}" : string.Empty);

        try
        {
            await runs.RecordAsync(
                new JobRun
                {
                    TaskKey = key,
                    UnitKey = unit.Key ?? string.Empty,
                    Trigger = context.Trigger,
                    StartedAt = startedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Outcome = outcome.Outcome,
                    ItemsProcessed = outcome.ItemsProcessed,
                    ItemsChanged = outcome.ItemsChanged,
                    FailureReason = outcome.FailureReason
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Job} could not record what {Unit} did", name, unit.Name);
        }
    }

}

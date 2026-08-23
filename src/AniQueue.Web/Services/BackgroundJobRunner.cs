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
    ILogger<BackgroundJobRunner<TJob>> logger) : BackgroundService
    where TJob : notnull, IBackgroundJob
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan period;
        string name;

        using (var scope = scopeFactory.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<TJob>();
            period = job.TickPeriod;
            name = job.Name;
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

        void OnLibraryChanged(LibraryChange? _)
        {
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

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                JobTrigger trigger;

                if (await Task.WhenAny(tick, wake) == tick)
                {
                    if (!await tick)
                    {
                        break;
                    }

                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                    trigger = JobTrigger.Timer;
                }
                else
                {
                    // Awaited rather than discarded so a cancellation surfaces here
                    // instead of as an unobserved faulted task.
                    await wake;
                    wake = woken.WaitAsync(stoppingToken);
                    trigger = JobTrigger.LibraryChange;
                }

                await RunUnitsAsync(name, trigger, stoppingToken);
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
    private async Task RunUnitsAsync(string name, JobTrigger trigger, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<TJob>();

        foreach (var unit in job.Units)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var context = new JobRunContext(trigger, unit.Key);

            try
            {
                var outcome = await job.RunAsync(context, stoppingToken);

                Report(name, unit, outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Everything a job expects to go wrong it reports itself, as a failed
                // outcome. Reaching here means something unforeseen — so it is logged
                // with its stack, and reported as a failure with a reason that is not
                // one, because §6 forbids a stack trace reaching a page.
                //
                // Caught at all because an escaping exception ends the hosted service
                // and by default takes the host down with it. A background job failing
                // must not stop the application serving the pages that still work.
                logger.LogError(ex, "{Job} threw while running {Unit}", name, unit.Name);

                Report(name, unit, JobRunOutcome.Failed("Something went wrong. The log has the details."));
            }
        }
    }

    /// <summary>
    /// Says what a run did.
    /// </summary>
    /// <remarks>
    /// The log is all there is in Phase 15a, deliberately: this phase changes the
    /// contract and nothing else, so a regression in 15b's storage is attributable to
    /// storage. Phase 15b writes a <c>JobRun</c> row here instead — from what the job
    /// returned rather than counted again — and that row is what moves the cadence
    /// clock, including for a run that threw.
    /// </remarks>
    private void Report(string name, JobUnit unit, JobRunOutcome outcome)
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
    }
}

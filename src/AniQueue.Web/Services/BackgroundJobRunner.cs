using AniQueue.Core.Jobs;
using AniQueue.Core.Library;

namespace AniQueue.Web.Services;

/// <summary>
/// Drives one <see cref="IBackgroundJob"/> on a timer: tick, open a scope, run,
/// catch, back off.
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
/// <b>A scope per tick</b>, because the job resolves scoped services that open
/// short-lived database contexts (D3). A scope held for the process lifetime would
/// accumulate tracked entities for as long as the application runs.
///
/// <b>A library change also ticks it</b>, ahead of the timer. Without that, syncing
/// several hundred new titles left every one of them without relations until the
/// next quarter-hour came round, and the Sources page's refresh button looked like
/// the only way to get them — which is how an automatic job comes to be operated by
/// hand.
///
/// This is deliberately <i>not</i> the sync calling the backfill. Nothing here knows
/// which job it is running or what any other job does: the signal says the library
/// changed, every runner hears it, and each one still gates on its own precondition
/// and finds nothing if it has nothing to do (D25). A job woken with no work is a
/// no-op, which is what makes a shared signal safe to broadcast.
/// </remarks>
public sealed class BackgroundJobRunner<TJob>(
    IServiceScopeFactory scopeFactory,
    ILibraryChangeNotifier changes,
    ILogger<BackgroundJobRunner<TJob>> logger) : BackgroundService
    where TJob : notnull, IBackgroundJob
{
    /// <summary>The most a failing job's interval is stretched to: sixteen ticks.</summary>
    private const int MaxBackoffShift = 4;

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

        void OnLibraryChanged(LibraryChange _)
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

        var consecutiveFailures = 0;

        // Exactly one outstanding wait on each source, carried across iterations.
        // PeriodicTimer permits only one pending WaitForNextTickAsync at a time, so
        // the tick task is renewed only once it has actually completed.
        var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var wake = woken.WaitAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await Task.WhenAny(tick, wake) == tick)
                {
                    if (!await tick)
                    {
                        break;
                    }

                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    // Awaited rather than discarded so a cancellation surfaces here
                    // instead of as an unobserved faulted task.
                    await wake;
                    wake = woken.WaitAsync(stoppingToken);
                }

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<TJob>().RunAsync(stoppingToken);

                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Everything a job expects to go wrong it records itself — a
                    // failed sync writes a run row saying so. Reaching here means
                    // something unforeseen, so the job is treated as broken rather
                    // than merely unlucky: log it and stop asking so often.
                    //
                    // Caught at all because an escaping exception ends the hosted
                    // service, and by default takes the host down with it. A
                    // background job failing must not stop the application serving
                    // the pages that still work.
                    consecutiveFailures++;

                    logger.LogError(
                        ex,
                        "{Job} failed ({Failures} in a row); backing off",
                        name,
                        consecutiveFailures);

                    await Task.Delay(BackoffFor(period, consecutiveFailures), stoppingToken);
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

    private static TimeSpan BackoffFor(TimeSpan period, int consecutiveFailures) =>
        period * (1 << Math.Min(consecutiveFailures, MaxBackoffShift));
}

using AniQueue.Core.Jobs;

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
/// </remarks>
public sealed class BackgroundJobRunner<TJob>(
    IServiceScopeFactory scopeFactory,
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

        var consecutiveFailures = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
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

        logger.LogInformation("{Job} has stopped", name);
    }

    private static TimeSpan BackoffFor(TimeSpan period, int consecutiveFailures) =>
        period * (1 << Math.Min(consecutiveFailures, MaxBackoffShift));
}

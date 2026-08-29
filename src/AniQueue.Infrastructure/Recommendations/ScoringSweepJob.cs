using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Recommendations;

/// <summary>
/// Works through the backlog with nobody present, a batch at a time.
/// </summary>
/// <remarks>
/// Gated rather than orchestrated: what it has left to do is a count of rows the
/// database can answer, so it is a no-op whenever there is nothing.
///
/// It may apply without anybody looking because a reply the schema rejects is
/// recorded and never written — "applied only in full" is enforced by
/// <see cref="IRecommendationService.ApplyAsync"/> rather than by a person reading a
/// table.
///
/// Every title sent comes back. The return limit is a manual lever and must not apply
/// here: send fifty and take the best twenty, and the other thirty stay unscored, are
/// picked again next tick, and the tail of the backlog is never reached.
///
/// It wakes on a library change and takes only what is new. Scoring a handful of
/// newly added titles is cheap; re-scoring everything an import has just made stale
/// is not, so a change-woken run leaves the stale for the cadence.
///
/// Nothing arbitrates the model. Two sweeps cannot overlap because the runner's loop
/// is sequential, and somebody who wants the model stops this one from the tasks
/// page, which works while a request is in flight rather than only between them.
/// </remarks>
public sealed class ScoringSweepJob(
    IRecommendationService recommendations,
    IScoringEndpoint endpoint,
    ILibraryChangeNotifier notifier,
    IJobRunStore runs,
    IOptionsMonitor<ScoringOptions> options,
    IOptionsMonitor<TaskOptions> tasks,
    ILogger<ScoringSweepJob> logger,
    TimeProvider? timeProvider = null) : IBackgroundJob
{
    /// <summary>What a run of this job records itself as.</summary>
    public const string ProviderName = "Scheduled";

    /// <summary>How many failures in a row end a sweep.</summary>
    /// <remarks>
    /// One bad batch must not burn the budget, and must not stop the sweep either —
    /// a single odd title producing a reply the schema rejects would otherwise block
    /// everything behind it forever. Three in a row is a broken model or a broken
    /// endpoint, which is a different thing and worth giving up on until the runner's
    /// own backoff brings it round again.
    /// </remarks>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>The smallest batch worth trying after halving.</summary>
    private const int MinimumBatchSize = 5;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>What this task's runs are filed under. Never shown.</summary>
    public string Key => "scoring";

    public string Name => "Scoring sweep";

    /// <summary>
    /// The polling resolution, not the schedule.
    /// </summary>
    /// <remarks>
    /// Short and cheap, because the schedule is a setting that can change while the
    /// application runs and one that lived in the runner's timer could only take
    /// effect on restart. Most ticks answer "not due" from configuration alone,
    /// without touching the database.
    /// </remarks>
    public TimeSpan TickPeriod => TimeSpan.FromMinutes(5);

    /// <summary>
    /// One, and unnamed. There is one model and one backlog to work through.
    /// </summary>
    public IReadOnlyList<JobUnit> Units { get; } = [new JobUnit(null, "Ranking")];

    public async Task<JobRunOutcome> RunAsync(
        JobRunContext context,
        CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;

        // Cheapest questions first, and both are answered without a query: the kill
        // switch, and whether anywhere exists to send to.
        if (!current.Enabled || !endpoint.IsConfigured)
        {
            // The two commonest reasons nothing is ever ranked, and neither shows up
            // anywhere except as a backlog that stays unscored.
            logger.LogDebug(
                "Scoring is not due: enabled {Enabled}, endpoint configured {Configured}",
                current.Enabled,
                endpoint.IsConfigured);

            return JobRunOutcome.NotDue;
        }

        // A library change is a reason to score what is new, and never a reason to
        // re-score what has merely gone stale: an import lands hundreds of ratings at
        // one timestamp, so without this rule a ten-second import would start an
        // unattended re-score of the whole back catalogue.
        if (!context.NewWorkOnly && !context.IgnoresSchedule)
        {
            // The shared cadence, not a scoring one. What stays scoring's own is what
            // a sweep may do once it runs — the batch size, the time budget, and which
            // titles count as stale.
            if (tasks.CurrentValue.Schedule.ToInterval() is not { } interval)
            {
                logger.LogDebug("Scoring is not due: the task cadence is switched off");

                return JobRunOutcome.NotDue;
            }

            // From JobRun rather than from the last applied RecommendationRun, so a
            // sweep that ran and scored nothing — or failed, or was cancelled — does
            // not read as one that never happened and get started again on the next
            // tick.
            var lastRun = await runs.LastRunAtAsync(Key, context.Unit, cancellationToken);

            if (lastRun is { } previous && _time.GetUtcNow() - previous < interval)
            {
                logger.LogDebug(
                    "Scoring is not due: last run {LastRun:u}, cadence {Interval}", previous, interval);

                return JobRunOutcome.NotDue;
            }
        }

        return await SweepAsync(current, context, cancellationToken);
    }

    private async Task<JobRunOutcome> SweepAsync(
        ScoringOptions current,
        JobRunContext context,
        CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + TimeSpan.FromMinutes(Math.Clamp(current.SweepMinutes, 1, 24 * 60));
        var batchSize = Math.Clamp(current.BatchSize, MinimumBatchSize, 500);

        var failures = 0;
        var applied = 0;
        var batches = 0;

        // Read once for the whole sweep, and reused by every batch below. A sweep is
        // many requests and should be one opinion: re-reading the history per batch
        // would let a sync landing mid-sweep move the evidence underneath it, and
        // scores from either side then land in one column and get sorted against each
        // other. It is also what makes the prefix ScoringRequestWriter arranges hold
        // still, since the history is around 95% of a batch's payload.
        //
        // Deliberately left null here. Taking it before the loop would read several
        // hundred rows on every tick that turns out to have nothing to do, so it is
        // read on first use instead, below the gate that decides there is work.
        ScoringHistorySnapshot? history = null;

        // Kept so the row can say what actually went wrong rather than that something
        // did. "192.168.0.240 did not answer within 1200 seconds" names the setting to
        // change; "could not answer every request" sends somebody to the log to find
        // out the same thing.
        string? lastFailure = null;

        while (!cancellationToken.IsCancellationRequested
            && _time.GetUtcNow() < deadline
            && failures < MaxConsecutiveFailures)
        {
            var coverage = await recommendations.GetCoverageAsync(
                Profile.DefaultProfileId, current.StaleAfterRatings, cancellationToken);

            // A change-woken run takes the never-scored and leaves the stale.
            // GetCoverageAsync already reports the two apart, so nothing new has to be
            // computed to tell them apart.
            var outstanding = context.NewWorkOnly
                ? coverage.Unranked
                : coverage.Unranked + coverage.Stale;

            // The gate: what is left to do is a count, and a job with
            // nothing to do does nothing rather than being told not to run.
            if (outstanding == 0)
            {
                break;
            }

            // Never larger than the work outstanding. The request is ordered
            // neediest-first, so a batch of twenty-five against five unranked titles
            // still fixes the right five — and then spends the model on twenty that
            // did not need it, which on a small backlog is most of the run. Asking for
            // exactly what is left also makes the log agree with the card above it.
            // First batch of this sweep pays for the history; the rest reuse it.
            history ??= await recommendations.BuildHistoryAsync(
                Profile.DefaultProfileId, current.HistorySize, cancellationToken);

            var outcome = await RunBatchAsync(
                current,
                Math.Min(batchSize, outstanding),
                history,
                cancellationToken);

            batches++;

            if (outcome.Failure is { } failure)
            {
                lastFailure = failure;
            }

            if (outcome.TooLarge && batchSize > MinimumBatchSize)
            {
                // The one failure this can act on by itself. A batch that did not fit
                // is a batch to halve, not a sweep to abandon — and it is not counted
                // as a failure, because the next attempt is a different question.
                batchSize = Math.Max(batchSize / 2, MinimumBatchSize);

                logger.LogInformation(
                    "Scoring sweep reduced its batch to {BatchSize} after a request that would not fit",
                    batchSize);

                continue;
            }

            if (outcome.Applied > 0)
            {
                applied += outcome.Applied;
                failures = 0;
            }
            else
            {
                failures++;
            }
        }

        // <b>Cancellation cannot reach the runner by throwing.</b> The endpoint catches
        // the token trip and hands back a failed batch, so this loop ends normally and
        // the runner's own OperationCanceledException handler never fires — which is
        // how a cancelled sweep came to be recorded as one that succeeded at nothing,
        // and why a user could not tell from the page whether they had pressed Cancel.
        //
        // Checked before the empty-batch case, because a sweep stopped before its first
        // batch was still stopped rather than idle.
        if (cancellationToken.IsCancellationRequested)
        {
            return new JobRunOutcome(JobOutcome.Cancelled, applied, applied);
        }

        if (batches == 0)
        {
            return JobRunOutcome.NothingToDo;
        }

        logger.LogInformation(
            "Scoring sweep scored {Applied} {Titles} across {Batches} {Batches_} ({Failures} failed)",
            applied,
            applied == 1 ? "title" : "titles",
            batches,
            batches == 1 ? "batch" : "batches",
            failures);

        // A score is library data, so the signal goes out like any other job's.
        // Nothing downstream consumes it today, and saying so here would be the
        // coupling this decision forbids — a job announces what it changed and never
        // who should care.
        if (applied > 0)
        {
            notifier.Publish(origin: Key);
        }

        // <b>Any failure with nothing to show for it is a failed run.</b> This asked for
        // MaxConsecutiveFailures, which is the threshold for giving up on a sweep and
        // is the wrong question for how to report one: two batches that both timed out
        // ended the sweep having achieved nothing and were recorded as a success. A run
        // that scored nothing and failed at least once has failed, whatever the error
        // budget thought about carrying on.
        return applied == 0 && lastFailure is { } reason
            ? JobRunOutcome.Failed(reason, batches)
            // Titles for both, rather than batches considered against titles applied.
            // A batch is this job's own bookkeeping and means nothing on a row beside
            // a sync counting titles; how many were scored is the answer to what a
            // sweep did.
            : JobRunOutcome.Succeeded(applied, applied);
    }

    /// <summary>Asks for one batch, and applies it if the whole of it is sound.</summary>
    /// <returns>
    /// What it applied, whether the request was refused for size, and why it failed if
    /// it did — in the endpoint's own words, because that is what the row has to show.
    /// </returns>
    private async Task<(int Applied, bool TooLarge, string? Failure)> RunBatchAsync(
        ScoringOptions current,
        int batchSize,
        ScoringHistorySnapshot history,
        CancellationToken cancellationToken)
    {
        // The candidate limit is the batch, and the return limit is deliberately not
        // set: everything offered has to come back, or the tail of the backlog is never
        // reached. The history is the user's own setting, because a sweep predicting
        // against different evidence from a manual run would give two answers to one
        // question — and the snapshot is that argument carried one step further, since
        // a sweep predicting against different evidence from *itself* is worse again.
        //
        // MaxHistory is still passed and is still the user's setting; it no longer
        // reaches a query, because the snapshot was taken with it.
        var options = ScoringRequestOptions.From(
            current.HistorySize,
            batchSize,
            returnTop: null,
            current.IncludePersonalNotes);

        // Candidates are read fresh every batch and must be: they shrink as the sweep
        // scores them, and a title that left the backlog mid-sweep should not be sent
        // again. Only the history is held still.
        var request = await recommendations.BuildRequestAsync(
            Profile.DefaultProfileId, options, history, cancellationToken);

        if (request.Candidates.Count == 0)
        {
            return (0, false, null);
        }

        var answer = await endpoint.AskAsync(request, cancellationToken);

        if (!answer.Succeeded)
        {
            logger.LogWarning("Scoring sweep batch failed: {Reason}", answer.Message);

            return (0, answer.Failure == ScoringEndpointFailure.TooLarge, answer.Message);
        }

        // Endpoint rather than Pasted: this reply came back from the request built
        // a few lines above, in this process, so there is no document that could have
        // been the wrong one. It also could not name the database if it wanted to — the
        // schema a constrained server is given declares no envelope.
        var preview = await recommendations.PreviewAsync(
            Profile.DefaultProfileId, ScoringRoute.Endpoint, answer.Reply!, request, cancellationToken);

        if (!preview.CanApply)
        {
            // Recorded and skipped, not applied in part. The next batch covers
            // different titles, so one reply the schema rejected does not block the
            // rows behind it — which is the difference between a sweep that stalls on
            // an awkward title and one that works around it.
            logger.LogWarning(
                "Scoring sweep batch produced nothing applicable: {Problems}",
                string.Join("; ", preview.Problems.Select(p => p.Message)));

            return (0, false, "The model answered, but not with a ranking AniQueue could apply.");
        }

        var result = await recommendations.ApplyAsync(
            Profile.DefaultProfileId,
            preview,
            ProviderName,
            answer.ModelIdentifier,
            progress: null,
            answer.Duration,
            cancellationToken);

        return (result.Applied, false, null);
    }
}

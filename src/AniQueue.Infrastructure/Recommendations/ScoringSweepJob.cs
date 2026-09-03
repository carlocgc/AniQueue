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
            // From JobRun rather than from the last applied RecommendationRun, so a
            // sweep that ran and scored nothing — or failed, or was cancelled — does
            // not read as one that never happened and get started again on the next
            // tick.
            //
            // The shared cadence, not a scoring one. What stays scoring's own is what
            // a sweep may do once it runs — the batch size, the time budget, and which
            // titles count as stale.
            var lastRun = await runs.LastRunAtAsync(Key, context.Unit, cancellationToken);

            if (!JobCadence.IsDue(tasks.CurrentValue.Schedule, lastRun, _time.GetUtcNow()))
            {
                logger.LogDebug(
                    "Scoring is not due: last run {LastRun:u}, cadence {Cadence}",
                    lastRun,
                    tasks.CurrentValue.Schedule);

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

        // The budget counts failures in a row and is reset by a success, which is the
        // right question for whether to carry on and the wrong one for the log: a sweep
        // that failed once and then worked reported no failures at all.
        var failed = 0;

        // Every title this sweep has already asked about, whatever came back.
        //
        // The picker is stable by design — never-scored first, keyed on the title — so
        // without this a failed batch is re-selected in full on the next attempt: the
        // error budget buys three attempts at one request, and everything behind one
        // awkward title is unreachable for as long as it stays unscored.
        //
        // <b>Asked, rather than failed, and that is the difference between a sweep that
        // ends and one that does not.</b> What is outstanding is a count of the backlog
        // and knows nothing of this sweep, so a title held back here still counts as
        // work — and the picker, offered nothing better, would hand back titles this
        // sweep had scored a moment earlier and score them again, for as long as the
        // time budget allowed. One question per title per sweep; the ones a short reply
        // left out are the next sweep's to ask.
        //
        // It lives for the sweep and is thrown away with it.
        var asked = new HashSet<int>();

        // Set when nothing answered at the address. Distinct from the error budget,
        // which counts questions the model was actually asked.
        var unreachable = false;

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
                asked,
                cancellationToken);

            // Nothing left this sweep has not already put to the model. The picker is
            // the authority on that rather than the count above, which is a total of
            // the backlog and knows nothing of this sweep.
            if (outcome.NothingToOffer)
            {
                break;
            }

            batches++;

            if (outcome.Failure is { } failure)
            {
                lastFailure = failure;
            }

            // Nothing was asked: the address is wrong, or nothing is listening. No
            // title is implicated and three attempts at a dead address are three ways
            // of learning one fact, so the sweep ends and the next tick tries again.
            if (outcome.Unreachable)
            {
                unreachable = true;
                failed++;

                break;
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

            // Put to the model, so this sweep is done with them either way. On a
            // failure that is what makes the next batch a different question — three
            // failures are then three questions, which is what tells one poisonous
            // title apart from a model that cannot do this at all.
            asked.UnionWith(outcome.Candidates);

            if (outcome.Applied > 0)
            {
                applied += outcome.Applied;
                failures = 0;
            }
            else
            {
                failures++;
                failed++;
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
            failed);

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
        //
        // <b>So has one that stopped because nothing was listening</b>, however much it
        // scored first. A sweep that ranked forty titles and then lost the endpoint is
        // not a sweep that finished, and reporting it green is how a backlog that has
        // quietly stopped being scored comes to look like one that is complete.
        return lastFailure is { } reason && (applied == 0 || unreachable)
            // Titles for both, rather than batches considered against titles applied.
            // A batch is this job's own bookkeeping and means nothing on a row beside
            // a sync counting titles; how many were scored is the answer to what a
            // sweep did, and a failed run has to answer it too.
            ? JobRunOutcome.Failed(reason, applied, applied)
            : JobRunOutcome.Succeeded(applied, applied);
    }

    /// <summary>What one batch did, in the terms the sweep loop has to act on.</summary>
    /// <param name="Applied">Scores written. Zero and no failure means nothing was asked.</param>
    /// <param name="TooLarge">The request did not fit, so the batch is worth halving.</param>
    /// <param name="Unreachable">Nothing answered at the address, so no title is implicated.</param>
    /// <param name="NothingToOffer">
    /// The picker had no titles left to send, this sweep having asked about them all.
    /// </param>
    /// <param name="Failure">
    /// Why it failed, in the endpoint's own words, because that is what the row has to show.
    /// </param>
    private sealed record BatchOutcome(
        int Applied = 0,
        bool TooLarge = false,
        bool Unreachable = false,
        bool NothingToOffer = false,
        string? Failure = null)
    {
        /// <summary>
        /// What was asked about, so the caller holding the sweep's own state can note
        /// that these have now had their turn.
        /// </summary>
        public IReadOnlyList<int> Candidates { get; init; } = [];
    }

    /// <summary>Asks for one batch, and applies it if the whole of it is sound.</summary>
    private async Task<BatchOutcome> RunBatchAsync(
        ScoringOptions current,
        int batchSize,
        ScoringHistorySnapshot history,
        IReadOnlySet<int> asked,
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
            current.IncludePersonalNotes) with
        {
            // What this sweep has already put to the model. Set on the options rather
            // than filtered out of the reply, so a title held back never takes a place
            // in the batch: ten asked for is ten sent.
            ExcludeCandidates = asked
        };

        // Candidates are read fresh every batch and must be: they shrink as the sweep
        // scores them, and a title that left the backlog mid-sweep should not be sent
        // again. Only the history is held still.
        var request = await recommendations.BuildRequestAsync(
            Profile.DefaultProfileId, options, history, cancellationToken);

        if (request.Candidates.Count == 0)
        {
            return new BatchOutcome(NothingToOffer: true);
        }

        var candidates = request.Candidates.Select(candidate => candidate.Id).ToArray();

        var answer = await endpoint.AskAsync(request, cancellationToken);

        if (!answer.Succeeded)
        {
            logger.LogWarning("Scoring sweep batch failed: {Reason}", answer.Message);

            return new BatchOutcome(
                TooLarge: answer.Failure == ScoringEndpointFailure.TooLarge,

                // Refused before anything was sent, or nothing listening at all. The
                // batch reached no model, so nothing about these titles is implicated
                // and setting them aside would punish rows nobody has looked at.
                Unreachable: answer.Failure
                    is ScoringEndpointFailure.AddressRefused
                    or ScoringEndpointFailure.Unreachable,
                Failure: answer.Message)
            {
                Candidates = candidates
            };
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

            return new BatchOutcome(
                Failure: "The model answered, but not with a ranking AniQueue could apply.")
            {
                Candidates = candidates
            };
        }

        var result = await recommendations.ApplyAsync(
            Profile.DefaultProfileId,
            preview,
            ProviderName,
            answer.ModelIdentifier,
            progress: null,
            answer.Duration,
            cancellationToken);

        // Carried here too, for the reply that passed every check and still wrote
        // nothing. The loop counts that as a failure, and a failure it cannot set
        // aside is one it would ask again.
        return new BatchOutcome(result.Applied) { Candidates = candidates };
    }
}

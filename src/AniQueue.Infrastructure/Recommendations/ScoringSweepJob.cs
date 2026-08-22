using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Recommendations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Recommendations;

/// <summary>
/// Works through the backlog with nobody present, a batch at a time.
/// </summary>
/// <remarks>
/// The job <see cref="IBackgroundJob"/> named in advance — "metadata and artwork
/// enrichment, and eventually scheduled re-ranking". It is D25's gated shape applied
/// to scoring: what it has left to do is a count of rows the database can answer, so
/// it is a no-op whenever there is nothing, and nothing orchestrates it.
///
/// <b>D21 is why this may apply without anybody looking.</b> Unattended sync applies
/// the unambiguous and holds the rest; a reply the schema rejects is recorded and
/// never written, so "applied only in full" is enforced by
/// <see cref="IRecommendationService.ApplyAsync"/> rather than by a person reading a
/// table. What is lost is human review, and what is kept is the invariant.
///
/// <b>Every title sent comes back.</b> The return limit is a manual lever and must
/// not apply here: send fifty, take the best twenty, and the other thirty stay
/// unscored, are picked again next tick as the never-scored ones, and the tail of the
/// backlog is never reached. That is the blind spot "a cap is a page size, not a
/// horizon" was written against, arriving by the other door.
///
/// <b>It does not wake on library changes.</b> Every runner hears the broadcast and
/// this one returns immediately unless it is due: relations backfill is cheap and
/// should be prompt, while a sweep is minutes of somebody's GPU and was asked for
/// once a day.
/// </remarks>
public sealed class ScoringSweepJob(
    IRecommendationService recommendations,
    IScoringEndpoint endpoint,
    IScoringGate gate,
    IOptionsMonitor<ScoringOptions> options,
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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;

        // Cheapest questions first, and all three are answered without a query: the
        // kill switch, whether anywhere exists to send to, and whether the user asked
        // for this at all.
        if (!current.Enabled || !endpoint.IsConfigured)
        {
            return;
        }

        if (current.Schedule.ToInterval() is not { } interval)
        {
            return;
        }

        var lastRun = await recommendations.GetLastRunAtAsync(
            Profile.DefaultProfileId, ProviderName, cancellationToken);

        var now = _time.GetUtcNow();

        if (lastRun is { } previous && now - previous < interval)
        {
            return;
        }

        await SweepAsync(current, cancellationToken);
    }

    private async Task SweepAsync(ScoringOptions current, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + TimeSpan.FromMinutes(Math.Clamp(current.SweepMinutes, 1, 24 * 60));
        var batchSize = Math.Clamp(current.BatchSize, MinimumBatchSize, 500);

        var failures = 0;
        var applied = 0;
        var batches = 0;

        while (!cancellationToken.IsCancellationRequested
            && _time.GetUtcNow() < deadline
            && failures < MaxConsecutiveFailures)
        {
            // Asked before every batch rather than once at the top. A sweep runs for an
            // hour; somebody pressing Rank now in minute two should not wait fifty-eight
            // more, and the sweep resumes next tick from wherever it stopped.
            if (gate.IsInteractiveWaiting)
            {
                logger.LogInformation("Scoring sweep standing down: somebody is waiting for the model");
                break;
            }

            var coverage = await recommendations.GetCoverageAsync(
                Profile.DefaultProfileId, current.StaleAfterRatings, cancellationToken);

            // The gate D25 requires: what is left to do is a count, and a job with
            // nothing to do does nothing rather than being told not to run.
            if (coverage.Unranked + coverage.Stale == 0)
            {
                break;
            }

            var outcome = await RunBatchAsync(current, batchSize, cancellationToken);

            batches++;

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

        if (batches > 0)
        {
            logger.LogInformation(
                "Scoring sweep scored {Applied} {Titles} across {Batches} {Batches_} ({Failures} failed)",
                applied,
                applied == 1 ? "title" : "titles",
                batches,
                batches == 1 ? "batch" : "batches",
                failures);
        }
    }

    /// <summary>Asks for one batch, and applies it if the whole of it is sound.</summary>
    private async Task<(int Applied, bool TooLarge)> RunBatchAsync(
        ScoringOptions current,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // The candidate limit is the batch, and the return limit is deliberately not
        // set: everything offered has to come back, or the tail of the backlog is never
        // reached. The history is the user's own setting, because a sweep predicting
        // against different evidence from a manual run would give two answers to one
        // question.
        var options = ScoringRequestOptions.From(
            current.HistorySize,
            batchSize,
            returnTop: null,
            current.IncludePersonalNotes);

        var request = await recommendations.BuildRequestAsync(
            Profile.DefaultProfileId, options, cancellationToken);

        if (request.Candidates.Count == 0)
        {
            return (0, false);
        }

        ScoringEndpointResult answer;

        // Held for the batch and released before the next one, which is what makes
        // standing down between batches possible at all.
        using (await gate.EnterSweepAsync(cancellationToken))
        {
            answer = await endpoint.AskAsync(request, cancellationToken);
        }

        if (!answer.Succeeded)
        {
            logger.LogWarning("Scoring sweep batch failed: {Reason}", answer.Message);

            return (0, answer.Failure == ScoringEndpointFailure.TooLarge);
        }

        var preview = await recommendations.PreviewAsync(
            Profile.DefaultProfileId, answer.Reply!, request, cancellationToken);

        if (!preview.CanApply)
        {
            // Recorded and skipped, not applied in part (D31). The next batch covers
            // different titles, so one reply the schema rejected does not block the
            // rows behind it — which is the difference between a sweep that stalls on
            // an awkward title and one that works around it.
            logger.LogWarning(
                "Scoring sweep batch produced nothing applicable: {Problems}",
                string.Join("; ", preview.Problems.Select(p => p.Message)));

            return (0, false);
        }

        var result = await recommendations.ApplyAsync(
            Profile.DefaultProfileId,
            preview,
            ProviderName,
            answer.ModelIdentifier,
            progress: null,
            answer.Duration,
            cancellationToken);

        return (result.Applied, false);
    }
}

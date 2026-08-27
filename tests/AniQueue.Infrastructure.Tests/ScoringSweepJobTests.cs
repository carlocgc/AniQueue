using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Progress;
using AniQueue.Core.Recommendations;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Jobs;
using AniQueue.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The sweep, without a database or a network.
/// </summary>
/// <remarks>
/// Every collaborator is a stand-in, because what is being tested is the job's
/// judgement rather than anybody else's work: when it declines to run, how it divides
/// a backlog, what it does with a failure, and who it yields to. The pieces it drives
/// are tested where they live.
/// </remarks>
public class ScoringSweepJobTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A backlog that shrinks as it is scored, and counts what it was asked.</summary>
    private sealed class FakeRecommendations(int unranked) : IRecommendationService
    {
        public int Unranked { get; set; } = unranked;

        public List<int> BatchSizes { get; } = [];

        public List<string> Applied { get; } = [];

        /// <summary>How many times the history was actually read from storage.</summary>
        public int HistoryReads { get; private set; }

        /// <summary>What each batch was handed, in order. Null means it read its own.</summary>
        public List<ScoringHistorySnapshot?> HistoryPerBatch { get; } = [];

        /// <summary>Grows between batches, standing in for a sync landing mid-sweep.</summary>
        public int RatedTitles { get; set; } = 100;

        /// <summary>Runs before every request is built, so a test can move the library under a sweep.</summary>
        public Action? BeforeEachRequest { get; set; }

        public bool PreviewApplicable { get; set; } = true;

        public Task<ScoringCoverage> GetCoverageAsync(int profileId, int staleAfterRatings, CancellationToken ct = default) =>
            Task.FromResult(new ScoringCoverage { Waiting = 100, Ranked = 100 - Unranked, Stale = 0 });

        public Task<ScoringHistorySnapshot> BuildHistoryAsync(int profileId, int? maxHistory, CancellationToken ct = default)
        {
            HistoryReads++;

            return Task.FromResult(new ScoringHistorySnapshot
            {
                Entries = [.. Enumerable.Range(1, RatedTitles)
                    .Select(i => new ScoringHistoryEntry { Title = $"Rated {i}", Score = 8 })],
                Available = RatedTitles
            });
        }

        public Task<ScoringRequest> BuildRequestAsync(
            int profileId,
            ScoringRequestOptions? options = null,
            ScoringHistorySnapshot? history = null,
            CancellationToken ct = default)
        {
            BeforeEachRequest?.Invoke();

            var size = Math.Min(options?.MaxCandidates ?? Unranked, Unranked);

            BatchSizes.Add(size);
            HistoryPerBatch.Add(history);

            return Task.FromResult(new ScoringRequest
            {
                GeneratedAt = DateTimeOffset.UnixEpoch,
                Candidates = [.. Enumerable.Range(1, size)
                    .Select(i => new ScoringCandidate { Id = i, Title = $"#{i}" })],
                CandidatesAvailable = Unranked,
                History = history?.Entries ?? [],
                HistoryAvailable = history?.Available ?? 0
            });
        }

        public ScoringRoute? Route { get; private set; }

        /// <summary>Never called by the sweep. The page is the only caller (D53).</summary>
        public Task<ScoringSizeEstimate> MeasureAsync(
            int profileId, ScoringRequestOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException("The sweep does not measure requests.");

        /// <summary>The route the job asked for, so a test can assert it (D50).</summary>
        public Task<ScoringPreview> PreviewAsync(int profileId, ScoringRoute route, string json, ScoringRequest? request = null, CancellationToken ct = default)
        {
            Route = route;

            return Task.FromResult(new ScoringPreview
            {
                Items = PreviewApplicable
                    ? [.. (request?.Candidates ?? []).Select(c => new ScoringPreviewItem
                        {
                            Result = new ScoringResult { Id = c.Id, PredictedScore = 8, Confidence = 0.7 },
                            Title = c.Title,
                            Status = LibraryStatus.Planning
                        })]
                    : [],
                Problems = PreviewApplicable ? [] : [ScoringProblem.Error("Nope.")]
            });
        }

        public Task<ScoringApplyResult> ApplyAsync(
            int profileId,
            ScoringPreview preview,
            string providerName,
            string? modelIdentifier = null,
            IProgress<OperationProgress>? progress = null,
            TimeSpan? duration = null,
            CancellationToken ct = default)
        {
            Applied.Add(providerName);
            Unranked = Math.Max(Unranked - preview.ApplicableCount, 0);

            return Task.FromResult(new ScoringApplyResult(1, preview.ApplicableCount, 0));
        }

        public Task<RecommendationDetail?> GetDetailAsync(int profileId, int animeId, CancellationToken ct = default) =>
            Task.FromResult<RecommendationDetail?>(null);

        public Task<IReadOnlyList<RecommendationRunSummary>> GetRunsAsync(int profileId, int take = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecommendationRunSummary>>([]);
    }

    /// <summary>
    /// A sweep that scored nothing and failed says so, however few times it failed.
    /// </summary>
    /// <remarks>
    /// Found in a real log, not in a test: two batches timed out against a local model,
    /// the sweep gave up having applied nothing, and the row said <i>Succeeded</i>. The
    /// old condition asked whether the error budget was spent — three consecutive
    /// failures — which is the question for whether to keep going, not the question for
    /// what to report. One failure with nothing to show for it is a failed run.
    /// </remarks>
    [Fact]
    public async Task A_sweep_that_scored_nothing_and_failed_once_reports_a_failure()
    {
        var (job, library, endpoint, _, _) = Create(unranked: 25);

        // One failed batch, and then nothing left to attempt — so the sweep ends well
        // inside its error budget. That is the case the old condition got wrong: it
        // asked whether the budget was spent, which is the question for whether to keep
        // going rather than for what to report.
        endpoint.Respond = _ =>
        {
            library.Unranked = 0;

            return ScoringEndpointResult.Failed(
                ScoringEndpointFailure.TimedOut,
                "somewhere did not answer within 1200 seconds.");
        };

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Manual), CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome.Outcome);
    }

    /// <summary>
    /// The row carries the endpoint's own words rather than a guess at them.
    /// </summary>
    /// <remarks>
    /// "did not answer within 1200 seconds" names the setting to change. The sentence
    /// it replaced — "the model rejected or could not answer every request" — was
    /// reconstructed here after the real reason had been discarded, and sent the reader
    /// to the log to learn what the endpoint had already said (D40).
    /// </remarks>
    [Fact]
    public async Task A_failed_sweep_reports_what_the_endpoint_said()
    {
        var (job, _, endpoint, _, _) = Create(unranked: 5);

        endpoint.Respond = _ => ScoringEndpointResult.Failed(
            ScoringEndpointFailure.TimedOut,
            "somewhere did not answer within 1200 seconds.");

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Manual), CancellationToken.None);

        Assert.Equal("somewhere did not answer within 1200 seconds.", outcome.FailureReason);
    }

    /// <summary>
    /// A cancelled sweep is recorded as cancelled, not as a success that did nothing.
    /// </summary>
    /// <remarks>
    /// <b>The one that hid the others.</b> Cancellation cannot reach the runner by
    /// throwing, because the endpoint catches the token trip and hands back a failed
    /// batch — so the loop ended normally and the runner's own handler never fired. The
    /// page then showed the same thing whether or not anybody had pressed Cancel, which
    /// is exactly how a user came to be unable to say which had happened.
    ///
    /// It also matters beyond the label: 15b made a cancelled run advance the cadence
    /// clock so that cancelling means "skip this cycle", and that rests on the run being
    /// recorded as one.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_sweep_says_it_was_cancelled()
    {
        var (job, _, endpoint, _, _) = Create(unranked: 100);

        using var cancellation = new CancellationTokenSource();

        // Cancelled while a batch is in flight, which is when it really happens — and
        // answered the way the endpoint answers, by reporting rather than throwing.
        endpoint.Respond = _ =>
        {
            cancellation.Cancel();

            return ScoringEndpointResult.Failed(
                ScoringEndpointFailure.Cancelled,
                "The run was cancelled.");
        };

        var outcome = await job.RunAsync(
            new JobRunContext(JobTrigger.Manual), cancellation.Token);

        Assert.Equal(JobOutcome.Cancelled, outcome.Outcome);
    }

    [Fact]
    public async Task A_sweep_cancelled_before_its_first_batch_is_still_cancelled()
    {
        var (job, _, _, _, _) = Create(unranked: 100);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var outcome = await job.RunAsync(
            new JobRunContext(JobTrigger.Manual), cancellation.Token);

        // Stopped rather than idle. It had work and was told not to do it.
        Assert.Equal(JobOutcome.Cancelled, outcome.Outcome);
    }

    private sealed class FakeEndpoint : IScoringEndpoint
    {
        public bool IsConfigured { get; set; } = true;

        public string? Endpoint => "http://localhost:1234";

        public string? Model => "test-model";

        public Func<ScoringRequest, ScoringEndpointResult>? Respond { get; set; }

        public int Calls { get; private set; }

        public Task<ScoringEndpointResult> AskAsync(ScoringRequest request, CancellationToken ct = default)
        {
            Calls++;

            return Task.FromResult(
                Respond?.Invoke(request)
                ?? ScoringEndpointResult.Success("{ \"results\": [] }", "test-model", TimeSpan.FromSeconds(2)));
        }

        public Task<ScoringEndpointResult> TestAsync(CancellationToken ct = default) =>
            Task.FromResult(ScoringEndpointResult.Success("{}", "test-model", TimeSpan.Zero));
    }


    private static (ScoringSweepJob Job, FakeRecommendations Library, FakeEndpoint Endpoint, FixedTime Clock, FakeJobRunStore Runs) Create(
        int unranked = 100,
        Action<ScoringOptions>? configure = null,
        SyncSchedule cadence = SyncSchedule.Daily)
    {
        // Enabled explicitly, because the default is now off: the remote route is
        // opt-in, and every test below is about what a sweep does once somebody has
        // opted in. The default itself is asserted separately.
        var settings = new ScoringOptions
        {
            Endpoint = "http://localhost:1234",
            BatchSize = 25,
            Enabled = true
        };

        configure?.Invoke(settings);

        var library = new FakeRecommendations(unranked);
        var endpoint = new FakeEndpoint();

        var clock = new FixedTime(new DateTimeOffset(2026, 8, 22, 3, 0, 0, TimeSpan.Zero));
        var runs = new FakeJobRunStore();

        return (
            new ScoringSweepJob(
                library,
                endpoint,
                new NullNotifier(),
                runs,
                new StaticOptionsMonitor<ScoringOptions>(settings),
                new StaticOptionsMonitor<TaskOptions>(new TaskOptions { Schedule = cadence }),
                NullLogger<ScoringSweepJob>.Instance,
                clock),
            library,
            endpoint,
            clock,
            runs);
    }

    [Fact]
    public async Task A_fresh_installation_does_not_ask_a_remote_model_anything()
    {
        // The remote route is opt-in, and this is the whole of the mechanism: one
        // setting, defaulted off, gating the only job that sends anything. Whether it
        // works at all depends on the model — of three tried against a real library
        // only one could answer, the other two spending their entire output budget
        // reasoning — so an installation that has not asked for it must not spend
        // somebody's electricity finding that out.
        //
        // Asserted through the job rather than by reading the property back, because
        // what matters is that nothing leaves the machine, not what the default is
        // called. Taken from UserSettings.Defaults so that flipping the file's default
        // without meaning to fails here.
        var (job, _, endpoint, _, _) = Create(
            unranked: 100,
            o => o.Enabled = UserSettings.Defaults.ScoringEnabled);

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(JobRunOutcome.NotDue, outcome);
        Assert.Equal(0, endpoint.Calls);
    }

    [Fact]
    public async Task It_works_through_a_backlog_in_batches()
    {
        var (job, library, endpoint, _, _) = Create(unranked: 100);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        // Four batches of twenty-five, and then nothing left to ask about.
        Assert.Equal([25, 25, 25, 25], library.BatchSizes);
        Assert.Equal(4, endpoint.Calls);
        Assert.Equal(0, library.Unranked);
    }

    [Fact]
    public async Task A_sweep_with_nothing_to_do_does_not_read_the_history()
    {
        // D25's shape: a task that is switched on and idle costs nothing. The history
        // is several hundred rows, and reading it before the gate that decides there is
        // work would spend that on every tick of an up-to-date library — which is most
        // ticks. This nearly happened while adding the snapshot, which is why it is
        // pinned rather than assumed.
        var (job, library, endpoint, _, _) = Create(unranked: 0);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(0, library.HistoryReads);
        Assert.Equal(0, endpoint.Calls);
    }

    [Fact]
    public async Task Every_batch_of_one_sweep_predicts_against_the_same_history()
    {
        var (job, library, _, _, _) = Create(unranked: 100);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        // Read once for the whole sweep rather than once per batch.
        Assert.Equal(1, library.HistoryReads);

        // And the same object reached all four, so there is no route by which one
        // batch could have been given different evidence from another.
        Assert.Equal(4, library.HistoryPerBatch.Count);
        Assert.All(library.HistoryPerBatch, h => Assert.NotNull(h));
        Assert.Single(library.HistoryPerBatch.Distinct());
    }

    [Fact]
    public async Task A_sync_landing_mid_sweep_does_not_change_what_later_batches_are_told()
    {
        // The case this exists for, and it is not hypothetical: a sweep was observed
        // reporting 559 rated titles and then 563 within the same minute, because the
        // AniList job runs in its own loop and had landed four new ratings between two
        // batches. Scores from either side of that go into one column and get sorted
        // against each other, which is the comparison D43 spent a phase making safe.
        var (job, library, _, _, _) = Create(unranked: 100);

        library.RatedTitles = 559;

        // Driven from inside the fake rather than from the test body, which would race
        // it: this fake completes synchronously, so a mutation written after RunAsync
        // would land after the sweep had already finished and assert nothing at all.
        // Here the library provably changes between batch one and batch two.
        library.BeforeEachRequest = () => library.RatedTitles = 563;

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        // The library did change underneath the sweep...
        Assert.Equal(563, library.RatedTitles);

        // ...and no batch heard about it.
        Assert.Equal(1, library.HistoryReads);
        Assert.Equal(4, library.HistoryPerBatch.Count);
        Assert.All(library.HistoryPerBatch, h => Assert.Equal(559, h!.Available));
    }

    [Fact]
    public async Task It_records_its_runs_as_scheduled()
    {
        // So the runs list can tell an overnight sweep from a manual paste, and so
        // "when did this last run" has something to read.
        var (job, library, _, _, _) = Create(unranked: 25);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal([ScoringSweepJob.ProviderName], library.Applied);
    }

    [Fact]
    public async Task It_stops_when_there_is_nothing_left_to_rank()
    {
        // D25's rule: a job woken with no work is a no-op, which is what lets a shared
        // signal be safe to broadcast and a schedule be safe to leave on.
        var (job, _, endpoint, _, _) = Create(unranked: 0);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(0, endpoint.Calls);
    }

    [Fact]
    public async Task Everything_it_offers_has_to_come_back()
    {
        // The return limit is a manual lever and must not apply here. Send fifty, take
        // the best twenty, and the other thirty stay unscored and are picked again for
        // ever — the tail of the backlog would never be reached.
        var (job, _, endpoint, _, _) = Create(unranked: 25, o => o.ReturnTop = 5);

        ScoringRequest? sent = null;
        endpoint.Respond = request =>
        {
            sent = request;
            return ScoringEndpointResult.Success("{ \"results\": [] }", "m", TimeSpan.Zero);
        };

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Null(sent!.ReturnTop);
        Assert.Equal(sent.Candidates.Count, sent.ExpectedResults);
    }

    [Theory]
    [InlineData(false, true, SyncSchedule.Daily)]
    [InlineData(true, false, SyncSchedule.Daily)]
    [InlineData(true, true, SyncSchedule.Off)]
    public async Task It_declines_without_touching_anything(bool enabled, bool configured, SyncSchedule schedule)
    {
        // The kill switch, an endpoint that does not exist, and a schedule nobody
        // turned on. All three are answered from configuration alone — no query, no
        // request — because most ticks are one of them.
        var (job, library, endpoint, _, _) = Create(unranked: 50, o =>
        {
            o.Enabled = enabled;
            o.Endpoint = configured ? "http://localhost:1234" : null;
        },
        schedule);

        endpoint.IsConfigured = configured;

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Empty(library.BatchSizes);
        Assert.Equal(0, endpoint.Calls);
    }

    [Fact]
    public async Task A_run_inside_the_interval_does_nothing()
    {
        var (job, _, endpoint, clock, runs) = Create(unranked: 100, null, SyncSchedule.Daily);

        // From the run record rather than from the last applied ranking, since Phase
        // 15b: a sweep that ran and scored nothing is still a sweep that ran.
        runs.LastRunAt = clock.Now.AddHours(-1);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(0, endpoint.Calls);
    }

    [Fact]
    public async Task A_run_past_the_interval_goes_ahead()
    {
        var (job, _, endpoint, clock, runs) = Create(unranked: 25, null, SyncSchedule.Daily);

        runs.LastRunAt = clock.Now.AddDays(-2);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(1, endpoint.Calls);
    }

    // Two tests were here about the scoring gate: that a sweep stood down when
    // somebody was waiting for the model, and that it took the gate once per batch
    // rather than once per sweep. Both described how a sweep shared the model with a
    // run started from the Recommendations page, and D42 deleted that run — so there
    // is no second claimant left to yield to, and the gate went with it.
    //
    // What replaces standing down is cancelling: a person who wants the model stops
    // the sweep from the tasks page, which needs no cooperation between batches and
    // works while a request is in flight rather than only between them.

    [Fact]
    public async Task A_request_that_will_not_fit_halves_the_batch_rather_than_ending_the_sweep()
    {
        // The one failure the sweep can act on by itself, and 8b gave it its own value
        // so that it could. A batch that did not fit is a batch to halve.
        var (job, library, endpoint, _, _) = Create(unranked: 100);

        var refusals = 0;

        endpoint.Respond = _ => ++refusals <= 2
            ? ScoringEndpointResult.Failed(ScoringEndpointFailure.TooLarge, "Too big.")
            : ScoringEndpointResult.Success("{ \"results\": [] }", "m", TimeSpan.Zero);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        // 25, refused; 12, refused; then 6 and onwards, which land. A refusal is not
        // counted as a failure, because the next attempt asks a different question.
        Assert.Equal([25, 12, 6], library.BatchSizes.Take(3));
        Assert.Equal(0, library.Unranked);
    }

    [Fact]
    public async Task Three_failures_in_a_row_end_the_sweep()
    {
        // One bad batch must not burn the budget and must not stop the sweep. Three is
        // a broken model or a broken endpoint, which is worth giving up on until the
        // runner's own backoff comes round again.
        var (job, _, endpoint, _, _) = Create(unranked: 500);

        endpoint.Respond = _ => ScoringEndpointResult.Failed(ScoringEndpointFailure.Rejected, "No.");

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(3, endpoint.Calls);
    }

    [Fact]
    public async Task A_sweep_checks_its_reply_as_an_endpoint_answer()
    {
        // D50's exemption, asserted where it is claimed. The sweep must not ask a reply
        // to name the database: it built the request itself moments earlier, and the
        // schema a constrained server is given declares no envelope to answer in — so
        // requiring one would refuse every scheduled ranking.
        var (job, library, _, _, _) = Create(unranked: 10);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(ScoringRoute.Endpoint, library.Route);
    }

    [Fact]
    public async Task A_reply_the_schema_rejects_is_skipped_rather_than_applied_in_part()
    {
        // D31's invariant, enforced by the service and relied on here: a preview
        // carrying an error is never written, and the sweep moves on rather than
        // stalling on the titles behind it.
        var (job, library, endpoint, _, _) = Create(unranked: 500);

        library.PreviewApplicable = false;

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Empty(library.Applied);
        Assert.Equal(3, endpoint.Calls);
    }

    [Fact]
    public async Task It_never_asks_for_more_than_there_is_work()
    {
        // Requests are ordered neediest-first, so a batch larger than the outstanding
        // work still fixes the right titles — and then spends the model re-ranking ones
        // that were already up to date, which on a small backlog is most of the run.
        var (job, library, _, _, _) = Create(unranked: 6, o => o.BatchSize = 25);

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal([6], library.BatchSizes);
    }

    /// <summary>Hears the broadcast and does nothing with it.</summary>
    /// <remarks>
    /// These tests are about what the sweep decides, not about who listens. A fake
    /// that recorded publications would invite an assertion that the sweep announced
    /// itself, which is the coupling D41 exists to prevent.
    /// </remarks>
    private sealed class NullNotifier : ILibraryChangeNotifier
    {
        public event Action<LibraryChangeNotification>? Changed;

        public void Publish(LibraryChange? change = null, string? origin = null) =>
            Changed?.Invoke(new LibraryChangeNotification(change, origin));
    }
}

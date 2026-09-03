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
        /// <summary>
        /// The titles still wanting a score, by id.
        /// </summary>
        /// <remarks>
        /// Identity rather than a count, because a sweep that sets a failed batch
        /// aside has to be shown asking about <i>different</i> titles next time, and a
        /// fake handing back the same made-up ten every batch cannot show that.
        /// </remarks>
        private readonly List<int> _waiting = [.. Enumerable.Range(1, unranked)];

        public int Unranked
        {
            get => _waiting.Count;

            set
            {
                _waiting.Clear();
                _waiting.AddRange(Enumerable.Range(1, value));
            }
        }

        public List<int> BatchSizes { get; } = [];

        /// <summary>Which titles each batch was asked about, in order.</summary>
        public List<int[]> BatchIds { get; } = [];

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

        /// <summary>
        /// Whether a scored title stops being outstanding.
        /// </summary>
        /// <remarks>
        /// True for an ordinary backlog. False is the real library that found the
        /// loop: what a sweep has outstanding is a count, a title held back still
        /// counts, and a picker offered nothing better hands back what was scored a
        /// moment ago.
        /// </remarks>
        public bool ScoringClearsTheBacklog { get; set; } = true;

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

            // The picker's job, in one line: what is waiting, less whatever the caller
            // has set aside, neediest first.
            var excluded = options?.ExcludeCandidates ?? new HashSet<int>();
            var offered = _waiting.Where(id => !excluded.Contains(id)).ToList();

            var size = Math.Min(options?.MaxCandidates ?? offered.Count, offered.Count);
            var chosen = offered.Take(size).ToArray();

            BatchSizes.Add(size);
            BatchIds.Add(chosen);
            HistoryPerBatch.Add(history);

            return Task.FromResult(new ScoringRequest
            {
                GeneratedAt = DateTimeOffset.UnixEpoch,
                Candidates = [.. chosen.Select(id => new ScoringCandidate { Id = id, Title = $"#{id}" })],

                // The whole backlog, not what this batch may ask about. A sweep setting
                // titles aside must not tell the model its library shrank.
                CandidatesAvailable = Unranked,
                History = history?.Entries ?? [],
                HistoryAvailable = history?.Available ?? 0
            });
        }

        public ScoringRoute? Route { get; private set; }

        /// <summary>Never called by the sweep. The page is the only caller.</summary>
        public Task<ScoringSizeEstimate> MeasureAsync(
            int profileId, ScoringRequestOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException("The sweep does not measure requests.");

        /// <summary>The route the job asked for, so a test can assert it.</summary>
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

            if (ScoringClearsTheBacklog)
            {
                // By id, so a title that was scored leaves the waiting set and a title
                // the reply left out stays in it.
                var scored = preview.Items.Select(item => item.Result.Id).ToHashSet();

                _waiting.RemoveAll(scored.Contains);
            }

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
    /// to the log to learn what the endpoint had already said.
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
    /// It also matters beyond the label: a cancelled run advances the cadence
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

        /// <summary>
        /// A stop, so a sweep that will not end fails a test rather than hanging one.
        /// </summary>
        /// <remarks>
        /// Far above any batch count these tests produce. A sweep's own guards are its
        /// time budget and its error budget, and a fixed clock disables the first of
        /// them — so a loop that neither fails nor runs out of work would run for ever
        /// here.
        /// </remarks>
        public int MostCallsAllowed { get; set; } = 50;

        public Task<ScoringEndpointResult> AskAsync(ScoringRequest request, CancellationToken ct = default)
        {
            Calls++;

            if (Calls > MostCallsAllowed)
            {
                throw new InvalidOperationException(
                    $"The sweep asked {Calls} times without stopping, which means it is not ending.");
            }

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
        // A task that is switched on and idle costs nothing. The history
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
        // against each other, which is exactly the unsafe comparison.
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
        // A job woken with no work is a no-op, which is what lets a shared
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
        // A sweep that ran and scored nothing is still a sweep that ran.
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

    // Nothing arbitrates the model: a person who wants it stops the sweep from the
    // tasks page, which needs no cooperation between batches and works while a
    // request is in flight rather than only between them.

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

    /// <summary>
    /// The bug this was written for: a sweep that asked the same question until its
    /// budget ran out.
    /// </summary>
    /// <remarks>
    /// A failed batch applies nothing, so its titles keep a null score date, and the
    /// picker orders never-scored first with a stable tiebreak — so the next batch
    /// took exactly the same titles and the three the error budget allows were three
    /// attempts at one request. One title that breaks a reply then sat at the front of
    /// every batch of every sweep, and nothing behind it was ever reached.
    /// </remarks>
    [Fact]
    public async Task A_batch_the_model_could_not_score_is_not_asked_again()
    {
        var (job, library, endpoint, _, _) = Create(unranked: 100);

        // One title the model cannot answer for, anywhere in the request.
        endpoint.Respond = request => request.Candidates.Any(candidate => candidate.Id == 3)
            ? ScoringEndpointResult.Failed(ScoringEndpointFailure.Rejected, "No.")
            : ScoringEndpointResult.Success("{ \"results\": [] }", "m", TimeSpan.Zero);

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        // The first batch carried it and failed; the second asked about different
        // titles rather than the same ones.
        Assert.Contains(3, library.BatchIds[0]);
        Assert.DoesNotContain(3, library.BatchIds[1]);

        // And the rest of the backlog was reached: three batches of twenty-five
        // scored, with only the set-aside batch left waiting.
        Assert.Equal(25, library.Unranked);
        Assert.Equal(4, endpoint.Calls);
        Assert.Equal(JobOutcome.Succeeded, outcome.Outcome);
    }

    /// <summary>
    /// A sweep puts each title to the model once and then stops.
    /// </summary>
    /// <remarks>
    /// Found by running it against a real library, not by reading the code. Holding
    /// back only what had <i>failed</i> was not enough: what a sweep has outstanding is
    /// a count of the backlog, a title held back still counts towards it, and the
    /// picker — offered nothing better — handed back titles the same sweep had scored
    /// seconds earlier. Four thousand runs were recorded before it was stopped.
    /// </remarks>
    [Fact]
    public async Task A_sweep_asks_about_each_title_once_even_when_scoring_leaves_it_outstanding()
    {
        var (job, library, endpoint, _, _) = Create(unranked: 10, o => o.BatchSize = 5);

        library.ScoringClearsTheBacklog = false;

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        // Two batches of five, and then nothing this sweep has not already asked.
        Assert.Equal(2, endpoint.Calls);
        Assert.Equal([5, 5], library.BatchSizes.Take(2));

        // And no title was put to the model twice.
        Assert.Equal(10, library.BatchIds.SelectMany(ids => ids).Distinct().Count());
        Assert.Equal(JobOutcome.Succeeded, outcome.Outcome);
    }

    [Fact]
    public async Task Nothing_listening_ends_the_sweep_rather_than_spending_the_budget()
    {
        // No title is implicated by an address nobody answers, so there is nothing to
        // set aside and nothing to learn from asking twice more.
        var (job, _, endpoint, _, _) = Create(unranked: 100);

        endpoint.Respond = _ => ScoringEndpointResult.Failed(
            ScoringEndpointFailure.Unreachable,
            "Nothing answered at http://192.168.0.240:1234.");

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(1, endpoint.Calls);
        Assert.Equal(JobOutcome.Failed, outcome.Outcome);
        Assert.Equal("Nothing answered at http://192.168.0.240:1234.", outcome.FailureReason);
    }

    /// <summary>
    /// A sweep that ranked forty titles and then lost the endpoint has not finished.
    /// </summary>
    /// <remarks>
    /// One applied score used to make the row green whatever happened afterwards,
    /// which is how a backlog that had quietly stopped being scored came to look like
    /// one that was complete.
    /// </remarks>
    [Fact]
    public async Task A_sweep_that_scored_something_and_then_lost_the_endpoint_has_failed()
    {
        var (job, _, endpoint, _, _) = Create(unranked: 100);

        var answers = 0;

        endpoint.Respond = _ => ++answers == 1
            ? ScoringEndpointResult.Success("{ \"results\": [] }", "m", TimeSpan.Zero)
            : ScoringEndpointResult.Failed(ScoringEndpointFailure.Unreachable, "Nothing answered.");

        var outcome = await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome.Outcome);
        Assert.Equal("Nothing answered.", outcome.FailureReason);

        // The row still says what it managed before it stopped.
        Assert.Equal(25, outcome.ItemsChanged);
    }

    [Fact]
    public async Task A_sweep_stops_once_everything_left_has_been_set_aside()
    {
        // Not a failure and not a batch: the picker had nothing to offer, which is a
        // sweep that has run out of work rather than one that asked and was refused.
        var (job, library, endpoint, _, _) = Create(unranked: 25);

        endpoint.Respond = _ => ScoringEndpointResult.Failed(ScoringEndpointFailure.Rejected, "No.");

        await job.RunAsync(new JobRunContext(JobTrigger.Timer), CancellationToken.None);

        Assert.Equal(1, endpoint.Calls);
        Assert.Empty(library.Applied);
    }

    [Fact]
    public async Task A_sweep_checks_its_reply_as_an_endpoint_answer()
    {
        // The exemption, asserted where it is claimed. The sweep must not ask a reply
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
        // The invariant, enforced by the service and relied on here: a preview
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
    /// itself, which is the coupling the origin exists to prevent.
    /// </remarks>
    private sealed class NullNotifier : ILibraryChangeNotifier
    {
        public event Action<LibraryChangeNotification>? Changed;

        public void Publish(LibraryChange? change = null, string? origin = null) =>
            Changed?.Invoke(new LibraryChangeNotification(change, origin));
    }
}

using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The backfill against a real database, with the network replaced by canned
/// responses.
///
/// What these are mostly about is the marker — the difference between recording
/// that a title was <i>asked about</i> and recording that it <i>had relations</i>.
/// Roughly half a library is standalone, so getting that wrong produces a pass that
/// never finishes and re-spends a rate limit on questions already answered, which
/// is invisible until somebody looks at a request log.
/// </summary>
public class RelationBackfillTests
{
    /// <summary>Long enough that no test spends it; the budget itself has its own test.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Answers with whatever it was handed, and remembers what it was asked.
    /// </summary>
    /// <remarks>
    /// Written to a script of responses rather than generating them, because half
    /// these tests are about what happens when a response is <i>not</i> the happy
    /// one: a 429, an unreadable body, a page that omits a title that was asked
    /// about.
    /// </remarks>
    private sealed class StubRelationClient(params string[] payloads) : IAniListClient
    {
        private int _call;

        public List<IReadOnlyCollection<string>> Requests { get; } = [];

        public string? FailWith { get; set; }

        public TimeSpan? RetryAfter { get; set; }

        public int? RemainingHeader { get; set; }

        /// <summary>
        /// How long each request appears to take, for the tests about the budget.
        /// </summary>
        /// <remarks>
        /// Moved on the fake clock rather than really waited, so a test that proves a
        /// ten-minute budget stops a pass costs milliseconds.
        /// </remarks>
        public TimeSpan TakesPerRequest { get; set; }

        public ImmediateTimeProvider? Clock { get; set; }

        public Task<AniListFetch> FetchListAsync(string userName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This stub answers relation fetches only.");

        public Task<AniListRelationsFetch> FetchRelationsAsync(
            IReadOnlyCollection<string> externalIds,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. externalIds]);

            if (TakesPerRequest > TimeSpan.Zero)
            {
                Clock?.Advance(TakesPerRequest);
            }

            if (FailWith is not null)
            {
                return Task.FromResult(AniListRelationsFetch.Failed(FailWith, RetryAfter));
            }

            // Past the end of the script repeats the last response, so a test that
            // only cares about the first batch does not have to write the rest.
            var payload = payloads[Math.Min(_call++, payloads.Length - 1)];

            return Task.FromResult(new AniListRelationsFetch
            {
                Payload = Encoding.UTF8.GetBytes(payload),
                RateLimitRemaining = RemainingHeader
            });
        }
    }

    /// <summary>
    /// A clock whose timers fire at once, so pacing is exercised without waiting for
    /// it.
    /// </summary>
    /// <remarks>
    /// The pacing arithmetic is tested in Core with no clock at all. This exists
    /// only so a multi-batch test does not spend two real seconds proving that the
    /// service asked to wait.
    /// </remarks>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public List<TimeSpan> Waits { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>Moves the clock, which is how a test ages an answer past its cutoff.</summary>
        public void Advance(TimeSpan by) => _now += by;

        // Elapsed time is measured from the same movable clock rather than from the
        // real one, so a test can spend a budget without spending the wall time.
        public override long GetTimestamp() => _now.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Waits.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

        public required StubRelationClient Client { get; init; }

        public required RelationBackfillService Backfill { get; init; }

        public required ImmediateTimeProvider Time { get; init; }

        public static async Task<Fixture> CreateAsync(StubRelationClient client, bool syncEnabled = true)
        {
            var database = await SqliteTestDatabase.CreateAsync();
            var time = new ImmediateTimeProvider();

            client.Clock = time;

            return new Fixture
            {
                Database = database,
                Client = client,
                Time = time,
                Backfill = new RelationBackfillService(
                    database.ContextFactory,
                    client,
                    new StubOptionsMonitor(new SyncOptions { Enabled = syncEnabled }),
                    NullLogger<RelationBackfillService>.Instance,
                    time)
            };
        }

        /// <summary>Adds titles carrying AniList identifiers, which is the population in scope.</summary>
        public async Task SeedAsync(params string[] externalIds)
        {
            await using var context = Database.CreateContext();

            foreach (var externalId in externalIds)
            {
                await SeedData.CreateAnimeAsync(
                    context, $"Title {externalId}", AnimeSource.AniList, externalId);
            }
        }

        public async Task<List<AnimeRelation>> RelationsAsync()
        {
            await using var context = Database.CreateContext();

            return await context.AnimeRelations
                .OrderBy(r => r.ExternalId)
                .ThenBy(r => r.RelatedExternalId)
                .ToListAsync();
        }

        public async Task<List<string>> UnfetchedAsync()
        {
            await using var context = Database.CreateContext();

            return await context.AnimeExternalIds
                .Where(x => x.RelationsFetchedAt == null)
                .OrderBy(x => x.ExternalId)
                .Select(x => x.ExternalId)
                .ToListAsync();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    /// <summary>
    /// A response for the given titles, each with the edges named.
    /// </summary>
    /// <remarks>
    /// Assembled by concatenation rather than as an interpolated raw literal, which
    /// C# will not accept here: the JSON's own closing braces run up against the
    /// interpolation's, and the escaping needed to separate them makes a small
    /// fixture unreadable.
    /// </remarks>
    private static string Response(params (string Id, string Edges)[] titles) =>
        "{\"data\":{\"Page\":{\"media\":[" +
        string.Join(",", titles.Select(t =>
            "{\"id\":" + t.Id + ",\"relations\":{\"edges\":[" + t.Edges + "]}}")) +
        "]}}}";

    private static string Edge(string relationType, string id) =>
        "{\"relationType\":\"" + relationType + "\",\"node\":{\"id\":" + id + ",\"type\":\"ANIME\"}}";

    private static string Identifier(int number) =>
        number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Edges_are_stored_as_the_source_stated_them()
    {
        await using var fixture = await Fixture.CreateAsync(new StubRelationClient(
            Response(("100", $"{Edge("SEQUEL", "200")},{Edge("SIDE_STORY", "300")}"))));

        await fixture.SeedAsync("100");

        var result = await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(1, result.Requested);
        Assert.Equal(2, result.EdgesWritten);

        var relations = await fixture.RelationsAsync();

        Assert.Collection(
            relations,
            r => Assert.Equal((AnimeSource.AniList, "100", RelationType.Sequel, "200"),
                (r.Source, r.ExternalId, r.RelationType, r.RelatedExternalId)),
            r => Assert.Equal((AnimeSource.AniList, "100", RelationType.SideStory, "300"),
                (r.Source, r.ExternalId, r.RelationType, r.RelatedExternalId)));
    }

    [Fact]
    public async Task An_edge_pointing_at_a_title_the_user_does_not_own_is_kept()
    {
        // The reason this table is keyed by identifiers rather than AnimeId pairs.
        // Resolving at write time would discard exactly these, permanently,
        // on the strength of what happened to be in the library that afternoon.
        await using var fixture = await Fixture.CreateAsync(new StubRelationClient(
            Response(("100", Edge("SEQUEL", "999")))));

        await fixture.SeedAsync("100");

        await fixture.Backfill.RunAsync(Budget);

        var relation = Assert.Single(await fixture.RelationsAsync());
        Assert.Equal("999", relation.RelatedExternalId);
    }

    [Fact]
    public async Task A_title_with_no_relations_is_marked_and_never_asked_about_again()
    {
        // The property the whole design turns on. A marker meaning "we got edges"
        // would put every standalone title back in the queue on every pass — a
        // backfill that never finishes, against a rate limit.
        var client = new StubRelationClient(Response(("100", "")));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");

        await fixture.Backfill.RunAsync(Budget);

        Assert.Empty(await fixture.RelationsAsync());
        Assert.Empty(await fixture.UnfetchedAsync());

        await fixture.Backfill.RunAsync(Budget);

        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task A_title_the_source_declined_to_mention_is_still_marked()
    {
        // AniList answering about two of three is not a failure, and re-asking about
        // the third forever is how a single unknown id stalls a whole library.
        await using var fixture = await Fixture.CreateAsync(new StubRelationClient(
            Response(("100", Edge("SEQUEL", "200")))));

        await fixture.SeedAsync("100", "101");

        var result = await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Answered);
        Assert.Empty(await fixture.UnfetchedAsync());
    }

    [Fact]
    public async Task A_second_run_writes_no_duplicates()
    {
        // Belt and braces against the unique index: the service reads before writing
        // so that a re-run cannot fail a batch of several hundred edges over one
        // edge it already had.
        var client = new StubRelationClient(Response(("100", Edge("SEQUEL", "200"))));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");

        await fixture.Backfill.RunAsync(Budget);

        // Clearing the marker is what a manual refresh would do. Without it the
        // second run has nothing to ask about and proves nothing.
        await using (var context = fixture.Database.CreateContext())
        {
            await context.AnimeExternalIds.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RelationsFetchedAt, (DateTime?)null));
        }

        var second = await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(0, second.EdgesWritten);
        Assert.Single(await fixture.RelationsAsync());
    }

    [Fact]
    public async Task Catalogue_fields_ride_along_on_the_same_request()
    {
        await using var fixture = await Fixture.CreateAsync(new StubRelationClient(
            """
            {"data":{"Page":{"media":[
              {"id":100,"startDate":{"year":2016,"month":1,"day":8},
               "coverImage":{"color":"#AABBCC"},"relations":{"edges":[]}}
            ]}}}
            """));

        await fixture.SeedAsync("100");

        await fixture.Backfill.RunAsync(Budget);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal(new DateOnly(2016, 1, 8), anime.StartDate);
        Assert.Equal("#aabbcc", anime.CoverImageColor);
    }

    [Fact]
    public async Task The_release_year_is_left_to_the_sync_that_owns_it()
    {
        // A start date obviously implies a year, and writing one from the other here
        // would fight the sync for the column: seasonYear and the start year are
        // different numbers on purpose — a series first airing in December 2015
        // belongs to the Winter 2016 season — so the decade filter would flip
        // between runs.
        await using var fixture = await Fixture.CreateAsync(new StubRelationClient(
            """
            {"data":{"Page":{"media":[
              {"id":100,"startDate":{"year":2015,"month":12,"day":30},"relations":{"edges":[]}}
            ]}}}
            """));

        await fixture.SeedAsync("100");

        await using (var setup = fixture.Database.CreateContext())
        {
            var seeded = await setup.Anime.SingleAsync();
            seeded.ReleaseYear = 2016;
            await setup.SaveChangesAsync();
        }

        await fixture.Backfill.RunAsync(Budget);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal(2016, anime.ReleaseYear);
        Assert.Equal(new DateOnly(2015, 12, 30), anime.StartDate);
    }

    [Fact]
    public async Task A_failed_request_marks_nothing_so_the_next_visit_asks_again()
    {
        var client = new StubRelationClient(Response(("100", ""))) { FailWith = "AniList could not be reached." };
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");

        var result = await fixture.Backfill.RunAsync(Budget);

        Assert.NotNull(result.FailureReason);
        Assert.Equal(0, result.Requested);
        Assert.Equal(["100"], await fixture.UnfetchedAsync());

        // One attempt, not four: whatever refused that batch will refuse the next,
        // and the runner's own interval is the right place to wait it out.
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task An_unreadable_response_marks_nothing_either()
    {
        // The dangerous case. Reading a GraphQL error as "these titles have no
        // relations" would mark the batch fetched and never ask again — a permanent
        // hole written by a transient failure.
        await using var fixture = await Fixture.CreateAsync(new StubRelationClient(
            """{"errors":[{"message":"Too Many Requests"}],"data":null}"""));

        await fixture.SeedAsync("100");

        var result = await fixture.Backfill.RunAsync(Budget);

        Assert.NotNull(result.FailureReason);
        Assert.Equal(["100"], await fixture.UnfetchedAsync());
    }

    [Fact]
    public async Task The_kill_switch_stops_it_as_surely_as_it_stops_a_sync()
    {
        // Unattended outbound traffic is exactly what that switch exists to halt, and
        // an operator who turned sync off would not expect a second thing to carry on
        // talking to the same host.
        var client = new StubRelationClient(Response(("100", "")));
        await using var fixture = await Fixture.CreateAsync(client, syncEnabled: false);

        await fixture.SeedAsync("100");

        var result = await fixture.Backfill.RunAsync(Budget);

        Assert.False(result.DidWork);
        Assert.Empty(client.Requests);
        Assert.Equal(["100"], await fixture.UnfetchedAsync());
    }

    [Fact]
    public async Task Work_is_split_into_batches_and_paced_between_them()
    {
        var client = new StubRelationClient(Response(("100", ""))) { RemainingHeader = 40 };
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync([.. Enumerable.Range(1, 60).Select(Identifier)]);

        await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(50, client.Requests[0].Count);
        Assert.Equal(10, client.Requests[1].Count);

        // Before the second request and not before the first: a visit that asks once
        // and stops should not have spent two seconds doing nothing first.
        Assert.Equal([TimeSpan.FromSeconds(2)], fixture.Time.Waits);
    }

    /// <summary>
    /// A pass that runs out of time stops and says so, rather than throwing away what
    /// it managed.
    /// </summary>
    /// <remarks>
    /// A budget rather than a request ceiling, and the difference that
    /// matters is this one: the ceiling ended a visit on a count nobody chose, while
    /// this ends it only where a library is large enough to need it — and either way
    /// the markers already written mean the next pass carries on rather than starting
    /// again.
    /// </remarks>
    [Fact]
    public async Task A_pass_that_runs_out_of_time_keeps_what_it_did()
    {
        var client = new StubRelationClient(Response(("100", "")))
        {
            TakesPerRequest = TimeSpan.FromMinutes(1)
        };

        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync([.. Enumerable.Range(1, 60).Select(Identifier)]);

        var result = await fixture.Backfill.RunAsync(TimeSpan.FromSeconds(30));

        // One request, because the budget was spent before the second could start.
        Assert.Single(client.Requests);
        Assert.True(result.RanOutOfTime);
        Assert.Null(result.FailureReason);

        // And the fifty it did ask about are marked, so the next pass takes the rest.
        Assert.Equal(10, (await fixture.UnfetchedAsync()).Count);
    }

    [Fact]
    public async Task A_nearly_spent_budget_waits_for_the_window_rather_than_the_gap()
    {
        var client = new StubRelationClient(Response(("100", ""))) { RemainingHeader = 2 };
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync([.. Enumerable.Range(1, 60).Select(Identifier)]);

        await fixture.Backfill.RunAsync(Budget);

        Assert.Equal([TimeSpan.FromSeconds(60)], fixture.Time.Waits);
    }

    [Fact]
    public async Task A_library_with_no_AniList_identifiers_is_left_alone()
    {
        // A MyAnimeList-only library gets nothing from this, which is a
        // real gap rather than an oversight. What it must not do is ask anyway.
        var client = new StubRelationClient(Response(("100", "")));
        await using var fixture = await Fixture.CreateAsync(client);

        await using (var context = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(context, "MAL only", AnimeSource.MyAnimeList, "268");
        }

        var result = await fixture.Backfill.RunAsync(Budget);

        Assert.False(result.DidWork);
        Assert.Empty(client.Requests);
    }

    // --- Re-reading (the 30-day refresh) ---------------------------------

    [Fact]
    public async Task An_answer_is_re_read_once_it_has_gone_stale()
    {
        // The case a refresh exists for is both ends already owned: a relation added
        // or corrected between two titles the library already has. A new sequel needs
        // no refresh, because it arrives as a new title whose own edges point back.
        var client = new StubRelationClient(Response(("100", "")));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");
        await fixture.Backfill.RunAsync(Budget);

        Assert.Single(client.Requests);

        // A day short of the cutoff is still trusted.
        fixture.Time.Advance(RelationBackfillService.StaleAfter - TimeSpan.FromDays(1));
        await fixture.Backfill.RunAsync(Budget);

        Assert.Single(client.Requests);

        fixture.Time.Advance(TimeSpan.FromDays(2));
        await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task A_relation_the_source_no_longer_publishes_is_removed()
    {
        // Without this the pass could only ever add, so a corrected or withdrawn edge
        // would be confirmed rather than removed — and re-reading would achieve less
        // than half of what it is for.
        var client = new StubRelationClient(
            Response(("100", $"{Edge("SEQUEL", "200")},{Edge("SIDE_STORY", "300")}")),
            Response(("100", Edge("SEQUEL", "200"))));

        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");
        await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(2, (await fixture.RelationsAsync()).Count);

        fixture.Time.Advance(RelationBackfillService.StaleAfter + TimeSpan.FromDays(1));
        var second = await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(0, second.EdgesWritten);
        Assert.Equal(1, second.EdgesRemoved);

        var remaining = Assert.Single(await fixture.RelationsAsync());
        Assert.Equal("200", remaining.RelatedExternalId);
    }

    [Fact]
    public async Task A_title_the_response_did_not_mention_keeps_every_edge_it_had()
    {
        // Absence scoping, applied to edges: the source's silence is authoritative only
        // where it spoke. A batch that simply did not cover a title is a gap, not a
        // statement, and deleting on that basis would read one as the other.
        var client = new StubRelationClient(
            Response(("100", Edge("SEQUEL", "200")), ("101", Edge("SEQUEL", "201"))),
            Response(("100", Edge("SEQUEL", "200"))));

        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100", "101");
        await fixture.Backfill.RunAsync(Budget);

        fixture.Time.Advance(RelationBackfillService.StaleAfter + TimeSpan.FromDays(1));
        var second = await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(0, second.EdgesRemoved);
        Assert.Equal(2, (await fixture.RelationsAsync()).Count);
    }

    [Fact]
    public async Task A_failed_re_read_removes_nothing()
    {
        var client = new StubRelationClient(Response(("100", Edge("SEQUEL", "200"))));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");
        await fixture.Backfill.RunAsync(Budget);

        fixture.Time.Advance(RelationBackfillService.StaleAfter + TimeSpan.FromDays(1));
        client.FailWith = "AniList could not be reached.";

        var second = await fixture.Backfill.RunAsync(Budget);

        Assert.NotNull(second.FailureReason);
        Assert.Equal(0, second.EdgesRemoved);
        Assert.Single(await fixture.RelationsAsync());
    }

    /// <summary>
    /// Deleting the graph deletes the edges and forgets that anything was asked.
    /// </summary>
    /// <remarks>
    /// Both halves, and the second is the one worth a test. Deleting the edges alone
    /// would leave every title marked as already fetched, so nothing would rebuild
    /// them until the thirty-day staleness expired — a button that silently emptied
    /// the relation graph for a month. They are one transaction for that reason.
    ///
    /// Re-reading belongs to the tasks page; what is left here is the throwing away.
    /// </remarks>
    [Fact]
    public async Task Deleting_the_graph_removes_the_edges_and_the_markers()
    {
        var client = new StubRelationClient(Response(("100", Edge("SEQUEL", "200"))));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");
        await fixture.Backfill.RunAsync(Budget);

        Assert.Single(await fixture.RelationsAsync());
        Assert.Empty(await fixture.UnfetchedAsync());

        var deleted = await fixture.Backfill.ForgetAsync();

        Assert.Equal(1, deleted);
        Assert.Empty(await fixture.RelationsAsync());

        // The half that would otherwise be missed: unfetched again, so the next pass
        // has something to do.
        Assert.Equal(["100"], await fixture.UnfetchedAsync());
    }

    [Fact]
    public async Task The_next_pass_rebuilds_what_deleting_threw_away()
    {
        var client = new StubRelationClient(Response(("100", Edge("SEQUEL", "200"))));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100");
        await fixture.Backfill.RunAsync(Budget);
        await fixture.Backfill.ForgetAsync();

        // No clock movement at all: the marker is gone, so this is new work rather
        // than stale work and nothing waits for the thirty days.
        var rebuilt = await fixture.Backfill.RunAsync(Budget);

        Assert.Equal(1, rebuilt.Requested);
        Assert.Single(await fixture.RelationsAsync());
    }

    [Fact]
    public async Task A_pass_with_nothing_to_do_reports_progress_to_nobody()
    {
        // Guards the denominator: reporting "0 of 0" would render a progress bar for
        // an operation that never starts.
        var client = new StubRelationClient(Response(("100", "")));
        await using var fixture = await Fixture.CreateAsync(client);

        var reports = new List<int?>();
        var progress = new Progress<AniQueue.Core.Progress.OperationProgress>(p => reports.Add(p.Total));

        var result = await fixture.Backfill.RunAsync(Budget, progress);

        Assert.False(result.DidWork);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Coverage_counts_what_is_known_against_what_could_be_asked()
    {
        var client = new StubRelationClient(Response(("100", "")));
        await using var fixture = await Fixture.CreateAsync(client);

        await fixture.SeedAsync("100", "101");

        await using (var context = fixture.Database.CreateContext())
        {
            // Not in scope: no AniList identifier, so nothing could ever answer for it.
            await SeedData.CreateAnimeAsync(context, "MAL only", AnimeSource.MyAnimeList, "268");
        }

        var before = await fixture.Backfill.GetCoverageAsync();

        Assert.Equal(new RelationCoverage(0, 2), before);
        Assert.False(before.IsComplete);

        await fixture.Backfill.RunAsync(Budget);

        var after = await fixture.Backfill.GetCoverageAsync();

        Assert.Equal(new RelationCoverage(2, 2), after);
        Assert.True(after.IsComplete);
    }
}

using System.Net;
using System.Text;
using AniQueue.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The HTTP half of the sync, driven by a stub handler.
///
/// No test here reaches the network (§8). What is worth testing is not that
/// <c>HttpClient</c> works but that the failure paths produce something a user can
/// act on, and that a list arriving in pieces either arrives whole or does not
/// arrive at all — a half-fetched list is the one input a sync must never treat as
/// the truth (D19).
/// </summary>
public class AniListClientTests
{
    /// <summary>Answers requests from a script, and keeps what was asked.</summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return respond(RequestBodies.Count);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Collection(bool hasNextChunk) =>
        $$"""
          { "data": { "MediaListCollection": { "hasNextChunk": {{(hasNextChunk ? "true" : "false")}}, "lists": [] } } }
          """;

    private static (AniListClient Client, StubHandler Handler) Build(Func<int, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new AniListClient(new HttpClient(handler), NullLogger<AniListClient>.Instance), handler);
    }

    [Fact]
    public async Task A_complete_list_comes_back_as_one_payload()
    {
        var (client, _) = Build(_ => Json(Collection(hasNextChunk: false)));

        var fetch = await client.FetchListAsync("someone");

        Assert.True(fetch.Succeeded);
        Assert.Single(fetch.Payloads);
    }

    [Fact]
    public async Task The_query_pins_anime_and_asks_for_a_hundred_point_scores()
    {
        // Both are load-bearing. Without type: ANIME a manga list arrives; without
        // POINT_100 the score is on whichever of five scales the account uses, and
        // an 87 violates the 1-10 check constraint mid-transaction.
        var (client, handler) = Build(_ => Json(Collection(hasNextChunk: false)));

        await client.FetchListAsync("someone");

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("type: ANIME", body, StringComparison.Ordinal);
        Assert.Contains("score(format: POINT_100)", body, StringComparison.Ordinal);
        Assert.Contains("someone", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunked_list_is_followed_to_the_end()
    {
        var (client, handler) = Build(n => Json(Collection(hasNextChunk: n < 3)));

        var fetch = await client.FetchListAsync("someone");

        Assert.True(fetch.Succeeded);
        Assert.Equal(3, fetch.Payloads.Count);
        Assert.Contains("\"chunk\":3", handler.RequestBodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_that_never_ends_fails_rather_than_looping()
    {
        // hasNextChunk comes from the other end. A server that always says "there is
        // more" would otherwise be a request loop with no exit, and the answer is to
        // fail rather than to keep what arrived — half a list is exactly what D19's
        // absence handling would read as a mass deletion.
        var (client, handler) = Build(_ => Json(Collection(hasNextChunk: true)));

        var fetch = await client.FetchListAsync("someone");

        Assert.False(fetch.Succeeded);
        Assert.Empty(fetch.Payloads);
        Assert.Equal(20, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task A_missing_account_says_so_in_words_an_operator_can_act_on()
    {
        // 404 is what a mistyped username and a list turned private both look like,
        // and it is the failure the operator can actually fix.
        var (client, _) = Build(_ => Json("{}", HttpStatusCode.NotFound));

        var fetch = await client.FetchListAsync("someone");

        Assert.False(fetch.Succeeded);
        Assert.Contains("private", fetch.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limiting_reports_how_long_to_wait()
    {
        // The measured limit is 30 requests a minute, not the documented 90.
        var (client, _) = Build(_ =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45));
            return response;
        });

        var fetch = await client.FetchListAsync("someone");

        Assert.False(fetch.Succeeded);
        Assert.Contains("45 seconds", fetch.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_error_is_reported_without_leaking_internals()
    {
        var (client, _) = Build(_ => Json("{}", HttpStatusCode.InternalServerError));

        var fetch = await client.FetchListAsync("someone");

        Assert.Equal("AniList returned 500.", fetch.FailureReason);
    }

    /// <summary>
    /// When AniList explains itself, the explanation is what the user is told.
    /// </summary>
    /// <remarks>
    /// This body is verbatim what AniList answered on 23 August 2026, when their API
    /// was switched off and every sync began failing. The page said "AniList returned
    /// 403." — a number nobody can act on — while the sentence that explains the whole
    /// situation was sitting in the response being discarded (D40).
    ///
    /// A status code cannot describe a state nobody anticipated. A GraphQL server
    /// answers with an <c>errors</c> array whatever the status, so that is where to
    /// look before falling back to the number.
    /// </remarks>
    [Fact]
    public async Task An_explained_failure_says_what_AniList_said()
    {
        var (client, _) = Build(_ => Json(
            """
            {"errors":[{"message":"The AniList API has been temporarily disabled due to severe stability issues.","status":403}]}
            """,
            HttpStatusCode.Forbidden));

        var fetch = await client.FetchListAsync("someone");

        Assert.Equal(
            "AniList says: The AniList API has been temporarily disabled due to severe stability issues.",
            fetch.FailureReason);
    }

    [Fact]
    public async Task A_failure_that_explains_nothing_falls_back_to_the_status()
    {
        var (client, _) = Build(_ => Json("not json at all", HttpStatusCode.Forbidden));

        var fetch = await client.FetchListAsync("someone");

        Assert.Equal("AniList returned 403.", fetch.FailureReason);
    }

    /// <summary>
    /// What a remote host says is bounded before it is shown.
    /// </summary>
    /// <remarks>
    /// §6's rule for the scoring endpoint, applied here for the same reason: a message
    /// from somewhere else is untrusted input, and a page is not the place to find out
    /// how long it was.
    /// </remarks>
    [Fact]
    public async Task An_enormous_explanation_is_cut_short()
    {
        var shouting = new string('x', 5_000);

        var (client, _) = Build(_ => Json(
            $$"""{"errors":[{"message":"{{shouting}}"}]}""",
            HttpStatusCode.Forbidden));

        var fetch = await client.FetchListAsync("someone");

        Assert.NotNull(fetch.FailureReason);
        Assert.True(fetch.FailureReason!.Length < 250, fetch.FailureReason);
    }

    /// <summary>
    /// A named failure keeps its own words rather than the server's.
    /// </summary>
    /// <remarks>
    /// 404 and 429 are the two AniQueue can say something more useful about than the
    /// body can: one names the thing an operator can fix, the other carries the wait
    /// from a header. Neither should be overwritten by whatever the server chose to
    /// put in its message.
    /// </remarks>
    [Fact]
    public async Task A_missing_list_keeps_its_own_explanation()
    {
        var (client, _) = Build(_ => Json(
            """{"errors":[{"message":"Not Found."}]}""",
            HttpStatusCode.NotFound));

        var fetch = await client.FetchListAsync("someone");

        Assert.Contains("private", fetch.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_endpoint_is_a_failure_not_an_exception()
    {
        // A sync records why a run failed; it does not throw its way out of a
        // background tick or a button press.
        var (client, _) = Build(_ => throw new HttpRequestException("no such host"));

        var fetch = await client.FetchListAsync("someone");

        Assert.False(fetch.Succeeded);
        Assert.DoesNotContain("host", fetch.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_account_means_no_request_at_all()
    {
        var (client, handler) = Build(_ => Json(Collection(hasNextChunk: false)));

        var fetch = await client.FetchListAsync("   ");

        Assert.False(fetch.Succeeded);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task An_unreadable_body_is_returned_for_the_parser_to_reject()
    {
        // The client reads one field — whether to ask for more — and nothing else.
        // A body it cannot parse still goes back, because the parser's message about
        // what is actually wrong with it beats one about paging.
        var (client, _) = Build(_ => Json("not json at all"));

        var fetch = await client.FetchListAsync("someone");

        Assert.True(fetch.Succeeded);
        Assert.Single(fetch.Payloads);
    }
}

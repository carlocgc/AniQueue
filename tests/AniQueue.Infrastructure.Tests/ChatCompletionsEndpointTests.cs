using System.Net;
using System.Text.Json;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The courier, against a server that is not there.
/// </summary>
/// <remarks>
/// Every case is one a real deployment produces: a server switched off, a model that
/// ran out of output room, a reply fenced in markdown. What is deliberately absent is
/// any test that a model ranks well — that is not this component's business, and the
/// contract it carries was tested in 7a without a network at all.
/// </remarks>
public class ChatCompletionsEndpointTests
{
    /// <summary>Answers whatever it is told to, and remembers what it was asked.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // A real handler observes the token before doing anything, and a stub that
            // does not would let a cancelled run appear to succeed — hiding exactly the
            // behaviour the cancellation test exists to check.
            cancellationToken.ThrowIfCancellationRequested();

            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>A chat completion the way every server in scope shapes one.</summary>
    private static string Completion(string content, string finish = "stop", string model = "qwen2.5-14b") =>
        JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } }
        });

    private static ScoringRequest Request(int candidates = 2) => new()
    {
        GeneratedAt = DateTimeOffset.UnixEpoch,
        Candidates = [.. Enumerable.Range(1, candidates)
            .Select(i => new ScoringCandidate { Id = i, Title = $"Title {i}" })],
        CandidatesAvailable = candidates,
        History = [new ScoringHistoryEntry { Title = "Gunbuster", Score = 9 }],
        HistoryAvailable = 1
    };

    private static (ChatCompletionsEndpoint Endpoint, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<ScoringOptions>? configure = null)
    {
        var settings = new ScoringOptions
        {
            Endpoint = "http://192.168.1.50:1234",
            Model = "qwen2.5-14b"
        };

        configure?.Invoke(settings);

        var handler = new StubHandler(respond);

        return (
            new ChatCompletionsEndpoint(
                new HttpClient(handler),
                new StaticOptionsMonitor<ScoringOptions>(settings),
                NullLogger<ChatCompletionsEndpoint>.Instance),
            handler);
    }

    [Fact]
    public async Task A_ranking_comes_back_verbatim()
    {
        var reply = """{ "results": [{ "id": 1, "predictedScore": 8.0, "confidence": 0.7 }] }""";

        var (endpoint, _) = Create(_ => Json(Completion(reply)));

        var result = await endpoint.AskAsync(Request());

        Assert.True(result.Succeeded);

        // Verbatim, because the parser is the only thing allowed to interpret it and it
        // has to see what the model actually wrote.
        Assert.Equal(reply, result.Reply);
    }

    [Fact]
    public async Task What_is_sent_is_the_prompt_and_the_payload_and_nothing_invented()
    {
        // The claim it turns on: the two routes carry one contract. If this endpoint
        // composed its own instructions, the manual path and this one would be two
        // pipelines wearing one name.
        var request = Request();
        var (endpoint, handler) = Create(_ => Json(Completion("""{ "results": [] }""")));

        await endpoint.AskAsync(request);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var messages = sent.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(ScoringPromptBuilder.Build(request), messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(ScoringRequestWriter.Write(request), messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task It_posts_to_the_completions_path_on_the_configured_origin()
    {
        var (endpoint, handler) = Create(_ => Json(Completion("""{ "results": [] }""")));

        await endpoint.AskAsync(Request());

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://192.168.1.50:1234/v1/chat/completions", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task The_output_ceiling_grows_with_what_was_asked_for()
    {
        // Sent because most servers default it far below what a real ranking needs, and
        // the symptom is a truncated reply rather than an error.
        var (endpoint, handler) = Create(_ => Json(Completion("""{ "results": [] }""")));

        await endpoint.AskAsync(Request(candidates: 50));

        using var sent = JsonDocument.Parse(handler.LastBody!);

        Assert.Equal(
            ChatCompletionsEndpoint.MaxTokensFor(50),
            sent.RootElement.GetProperty("max_tokens").GetInt32());
    }

    /// <summary>
    /// The ceiling leaves room for a model that thinks before it answers.
    /// </summary>
    /// <remarks>
    /// Against measurements rather than against the formula, because the test above
    /// compares <c>MaxTokensFor</c> to itself and would stay green at any floor at all
    /// — including the 512 that caused this. These numbers are from twenty-eight
    /// replies from gpt-oss-20b at ten titles a batch: the analysis it emits into the
    /// same completion ran 211–1,436 tokens, and the JSON answer needed up to 620.
    /// A ceiling that does not clear both is one that truncates on an unlucky request
    /// while looking generous on an average one, which is exactly how the old floor
    /// survived review.
    /// </remarks>
    [Fact]
    public void The_output_ceiling_clears_the_worst_measured_reasoning_and_a_full_answer()
    {
        const int WorstObservedReasoning = 1436;
        const int LargestObservedAnswer = 620;

        var ceiling = ChatCompletionsEndpoint.MaxTokensFor(10);

        Assert.True(
            ceiling >= WorstObservedReasoning + LargestObservedAnswer,
            $"a batch of ten allows {ceiling} tokens, which does not clear the "
            + $"{WorstObservedReasoning} a reasoning model was measured spending on analysis "
            + $"plus the {LargestObservedAnswer} its answer needed.");
    }

    [Fact]
    public async Task Structured_output_is_asked_for_by_default_and_can_be_turned_off()
    {
        var (on, onHandler) = Create(_ => Json(Completion("""{ "results": [] }""")));
        await on.AskAsync(Request());

        using (var sent = JsonDocument.Parse(onHandler.LastBody!))
        {
            // json_schema, not json_object. LM Studio answers json_object with
            // "400: 'response_format.type' must be 'json_schema' or 'text'", which is
            // how the original choice was found to be wrong — so this asserts the shape
            // a real server actually accepts rather than the one that reads simplest.
            var format = sent.RootElement.GetProperty("response_format");

            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.Equal(
                ScoringResponseSchema.Name,
                format.GetProperty("json_schema").GetProperty("name").GetString());

            // Sent as an object rather than as a string containing an object, which is
            // the mistake that makes a server reject a schema it would otherwise honour.
            var schema = format.GetProperty("json_schema").GetProperty("schema");

            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.True(schema.GetProperty("properties").TryGetProperty("results", out _));
        }

        var (off, offHandler) = Create(
            _ => Json(Completion("""{ "results": [] }""")),
            o => o.UseStructuredOutput = false);

        await off.AskAsync(Request());

        using (var sent = JsonDocument.Parse(offHandler.LastBody!))
        {
            // A server that rejects the field is why this is a setting rather than
            // something sniffed, so turning it off has to actually remove it.
            Assert.False(sent.RootElement.TryGetProperty("response_format", out _));
        }
    }

    [Fact]
    public async Task The_model_that_answered_is_preferred_over_the_one_configured()
    {
        // They differ often enough to matter: a server configured as "local-model" may
        // answer as something specific, and the specific name is what makes a score
        // worth revisiting on better hardware.
        var (endpoint, _) = Create(_ => Json(Completion("""{ "results": [] }""", model: "qwen2.5-14b-instruct-q4")));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal("qwen2.5-14b-instruct-q4", result.ModelIdentifier);
    }

    [Fact]
    public async Task A_model_that_ran_out_of_room_says_so_rather_than_looking_malformed()
    {
        // The failure people hit most. Reported as "not valid JSON" it looks like the
        // model misbehaved, when the model did as well as it was allowed to.
        var (endpoint, _) = Create(_ => Json(Completion(
            """{ "results": [{ "id": 1, "predictedScore": 8.0, "confi""",
            finish: "length")));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.Truncated, result.Failure);
        Assert.Contains("ran out of room", result.Message!, StringComparison.OrdinalIgnoreCase);

        // It names AniQueue's own ceiling, and says so. max_tokens travels with every
        // request, so pointing at the server's own output limit would send somebody to
        // change a setting that is overridden before it can apply.
        Assert.Contains("AniQueue allows", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            ChatCompletionsEndpoint.MaxTokensFor(2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Message!);
        Assert.DoesNotContain("server's output limit", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_listening_is_reported_as_nothing_listening()
    {
        var (endpoint, _) = Create(_ => throw new HttpRequestException("refused"));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.Unreachable, result.Failure);
        Assert.Contains("192.168.1.50", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_http_error_carries_what_the_server_said()
    {
        // The diagnostic that makes somebody's own misconfiguration fixable, and the
        // one bounded because a settable address plus an unbounded echo is a way to
        // read things this application should not be reading.
        var (endpoint, _) = Create(_ => Json(
            """{ "error": { "message": "model 'qwen' not found" } }""",
            HttpStatusCode.NotFound));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.Rejected, result.Failure);
        Assert.Contains("404", result.Message!, StringComparison.Ordinal);
        Assert.Contains("not found", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reply_that_is_not_a_chat_completion_is_reported_as_such()
    {
        // An HTML error page from a reverse proxy is the usual cause, and it is a
        // different problem from a model answering badly.
        var (endpoint, _) = Create(_ => Json("<html><body>502 Bad Gateway</body></html>"));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.Unreadable, result.Failure);
    }

    [Fact]
    public async Task An_endpoint_the_guards_refuse_is_never_reached()
    {
        // The guards check what may be sent, so the handler must never see it.
        var (endpoint, handler) = Create(
            _ => Json(Completion("""{ "results": [] }""")),
            o => o.Endpoint = "http://169.254.169.254");

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.AddressRefused, result.Failure);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task No_endpoint_configured_is_a_state_rather_than_a_failure()
    {
        // The normal condition of a fresh install, and the card describes it rather
        // than a button producing an error.
        var (endpoint, handler) = Create(
            _ => Json(Completion("""{ "results": [] }""")),
            o => o.Endpoint = null);

        Assert.False(endpoint.IsConfigured);

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.NotConfigured, result.Failure);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Cancelling_is_told_apart_from_timing_out()
    {
        // One is the user's decision and the other is a setting to change, so the page
        // must not offer to raise a timeout somebody deliberately interrupted.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (endpoint, _) = Create(_ => Json(Completion("""{ "results": [] }""")));

        var result = await endpoint.AskAsync(Request(), cancelled.Token);

        Assert.Equal(ScoringEndpointFailure.Cancelled, result.Failure);
    }

    [Fact]
    public async Task Test_asks_a_real_question_and_reports_what_came_back()
    {
        // A ping would say the port is open. What is worth knowing is whether the thing
        // behind it can produce JSON, which nothing short of asking finds out.
        var (endpoint, handler) = Create(_ => Json(Completion(
            """{ "results": [{ "id": 1, "predictedScore": 8.0, "confidence": 0.7 }] }""")));

        var result = await endpoint.TestAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("Cowboy Bebop", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_distinguishes_a_server_that_answers_from_one_that_can_rank()
    {
        // The failure that would otherwise only surface after a ten-minute real run.
        var (endpoint, _) = Create(_ => Json(Completion("I'd suggest starting with Cowboy Bebop!")));

        var result = await endpoint.TestAsync();

        // It answered, so this is not Unreachable — the reply simply is not a ranking,
        // which the parser decides rather than this component.
        Assert.True(result.Succeeded);
        Assert.Equal("I'd suggest starting with Cowboy Bebop!", result.Reply);
        Assert.True(new ScoringResponseParser().Parse(result.Reply!).HasErrors);
    }

    [Fact]
    public async Task A_fenced_reply_survives_the_whole_journey()
    {
        // The two halves meeting: the courier carries the reply
        // untouched, and the parser is what finds the ranking inside it.
        var (endpoint, _) = Create(_ => Json(Completion(
            """
            ```json
            { "results": [{ "id": 1, "predictedScore": 8.0, "confidence": 0.7 }] }
            ```
            """)));

        var result = await endpoint.AskAsync(Request());
        var parsed = new ScoringResponseParser().Parse(result.Reply!);

        Assert.False(parsed.HasErrors);
        Assert.Equal([1], parsed.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public async Task A_request_too_big_for_the_context_says_so_and_says_what_to_change()
    {
        // The body LM Studio actually returned, against a real 8K model. Reported as
        // "answered 400 Bad Request" this is a dead end: the remedy sits inside a
        // disclosure, and none of the three things that would fix it are named.
        var (endpoint, _) = Create(_ => Json(
            """
            {"error":{"code":400,"message":"request (13782 tokens) exceeds the available context size (8192 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":13782,"n_ctx":8192}}
            """,
            HttpStatusCode.BadRequest));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.TooLarge, result.Failure);

        // Both numbers, because how far over decides which remedy is enough.
        Assert.Contains("13782", result.Message!, StringComparison.Ordinal);
        Assert.Contains("8192", result.Message!, StringComparison.Ordinal);

        // And the remedies, in the order they cost the user anything.
        Assert.Contains("history", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("context window", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_context_failure_without_numbers_still_says_what_to_change()
    {
        // Servers word this differently and some wrap it past recognition. The advice
        // does not depend on the numbers, so their absence must not cost the message.
        var (endpoint, _) = Create(_ => Json(
            """{"error":"This model's maximum context length is 8192 tokens."}""",
            HttpStatusCode.BadRequest));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.TooLarge, result.Failure);
        Assert.Contains("too big", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_ordinary_rejection_is_not_mistaken_for_one_that_did_not_fit()
    {
        // The floor on the match: a 400 about something else keeps the generic message
        // and the server's own words, because inventing advice about a problem we did
        // not diagnose is worse than reporting the problem.
        var (endpoint, _) = Create(_ => Json(
            """{"error":"model 'qwen' not found"}""",
            HttpStatusCode.BadRequest));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.Rejected, result.Failure);
    }

    [Fact]
    public async Task A_server_that_refuses_the_json_format_says_which_setting_to_turn_off()
    {
        // What LM Studio answered json_object with, before 8c sent json_schema. A
        // rejection naming a field the user cannot connect to a checkbox is a dead end.
        var (endpoint, _) = Create(_ => Json(
            """{"error":"'response_format.type' must be 'json_schema' or 'text'"}""",
            HttpStatusCode.BadRequest));

        var result = await endpoint.AskAsync(Request());

        Assert.Equal(ScoringEndpointFailure.Rejected, result.Failure);
        Assert.Contains("JSON only", result.Message!, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>An options monitor that never changes, for tests that do not need one to.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

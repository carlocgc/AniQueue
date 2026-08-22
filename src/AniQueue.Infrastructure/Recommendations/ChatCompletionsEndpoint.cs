using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AniQueue.Core.Recommendations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Recommendations;

/// <summary>
/// Posts a scoring request to a model the operator hosts, and brings back the reply.
/// </summary>
/// <remarks>
/// <b>The chat-completions shape, because everything speaks it.</b> LM Studio, Ollama,
/// llama.cpp, vLLM and text-generation-webui all serve <c>/v1/chat/completions</c>, so
/// targeting it is what makes "anything speaking a chat-completions API" true rather
/// than aspirational (D31).
///
/// <b>What is sent is what the Manual card shows, byte for byte.</b> The prompt is the
/// system message and the payload is the user message, both from the same builders the
/// page renders. If those ever diverged, the two routes would be two pipelines with one
/// name, which is the thing D31 exists to prevent — so nothing here composes text of
/// its own beyond the envelope the API requires.
/// </remarks>
public sealed partial class ChatCompletionsEndpoint(
    HttpClient client,
    IOptionsMonitor<ScoringOptions> options,
    ILogger<ChatCompletionsEndpoint> logger,
    TimeProvider? timeProvider = null) : IScoringEndpoint
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Roughly what one ranked title costs to generate, in tokens.
    /// </summary>
    /// <remarks>
    /// An id, a rank, two decimals, the JSON around them and a sentence of reasoning.
    /// Measured generously rather than tightly: the cost of overestimating is a ceiling
    /// no model reaches, and the cost of underestimating is a reply that stops
    /// mid-object — which is the single most common way this fails.
    /// </remarks>
    private const int TokensPerResult = 120;

    /// <summary>Headroom for the envelope, and for a model that pads.</summary>
    private const int TokenFloor = 512;

    /// <summary>How much of a failing response is shown back (D38).</summary>
    private const int MaxDiagnosticLength = 2048;

    /// <summary>
    /// Low, fixed, and not a setting.
    /// </summary>
    /// <remarks>
    /// A correctness knob rather than a preference: the same backlog should rank
    /// roughly the same way twice, and a number nobody can evaluate is a line in the
    /// settings file that only makes the file longer.
    /// </remarks>
    private const double Temperature = 0.2;

    public bool IsConfigured => options.CurrentValue.HasEndpoint;

    public string? Endpoint => options.CurrentValue.Endpoint;

    public string? Model => options.CurrentValue.Model;

    public Task<ScoringEndpointResult> AskAsync(
        ScoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            ScoringPromptBuilder.Build(request),
            ScoringRequestWriter.Write(request),
            MaxTokensFor(request.ExpectedResults),
            cancellationToken);
    }

    public Task<ScoringEndpointResult> TestAsync(CancellationToken cancellationToken = default)
    {
        // A real completion rather than a ping, through the same client and the same
        // output settings, because the failure worth catching here is a server that
        // answers and cannot produce JSON — and nothing short of asking it finds that.
        var probe = new ScoringRequest
        {
            GeneratedAt = _time.GetUtcNow(),
            Candidates =
            [
                new ScoringCandidate { Id = 1, Title = "Cowboy Bebop" },
                new ScoringCandidate { Id = 2, Title = "Serial Experiments Lain" }
            ],
            CandidatesAvailable = 2,
            History = [new ScoringHistoryEntry { Title = "Gunbuster", Score = 9 }],
            HistoryAvailable = 1
        };

        return SendAsync(
            ScoringPromptBuilder.Build(probe),
            ScoringRequestWriter.Write(probe),
            MaxTokensFor(probe.ExpectedResults),
            cancellationToken);
    }

    /// <summary>
    /// The output ceiling for a reply of this size.
    /// </summary>
    /// <remarks>
    /// Sent because most servers default it far below what a real ranking needs, and
    /// the symptom is not an error: the model stops mid-object and the reply is
    /// malformed JSON for a reason the user did not cause and cannot see.
    /// </remarks>
    internal static int MaxTokensFor(int results) => TokenFloor + (Math.Max(results, 1) * TokensPerResult);

    private async Task<ScoringEndpointResult> SendAsync(
        string prompt,
        string payload,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;

        if (!ScoringEndpointAddress.TryResolve(current.Endpoint, out var target, out var refusal))
        {
            // Refused before anything leaves the machine, which is the point of D38's
            // guards: the check is on what may be sent, not on what came back.
            return ScoringEndpointResult.Failed(
                current.HasEndpoint ? ScoringEndpointFailure.AddressRefused : ScoringEndpointFailure.NotConfigured,
                refusal);
        }

        var started = _time.GetTimestamp();

        // Its own timeout rather than the client's, so one setting covers the whole
        // attempt and a change to it takes effect without rebuilding the client.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(current.TimeoutSeconds, 5, 3600)));

        using var content = new StringContent(
            Body(current, prompt, payload, maxTokens),
            Encoding.UTF8,
            new MediaTypeHeaderValue("application/json"));

        try
        {
            using var response = await client.PostAsync(target, content, timeout.Token);

            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Scoring endpoint {Endpoint} answered {Status}",
                    target,
                    (int)response.StatusCode);

                // A request that did not fit is not a server that refused: it is the
                // one 400 with a specific remedy, and reporting it as "answered 400 Bad
                // Request" leaves that remedy inside a disclosure nobody opens.
                if (TooLarge(body) is { } tooLarge)
                {
                    return ScoringEndpointResult.Failed(
                        ScoringEndpointFailure.TooLarge,
                        tooLarge,
                        Elapsed(started),
                        Trim(body));
                }

                return ScoringEndpointResult.Failed(
                    ScoringEndpointFailure.Rejected,
                    $"{target.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}."
                        + Hint(current, body),
                    Elapsed(started),
                    Trim(body));
            }

            return ReadCompletion(body, Elapsed(started), target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Told to stop, rather than failed. Distinguished from a timeout because
            // one is the user's decision and the other is a setting to change.
            return ScoringEndpointResult.Failed(
                ScoringEndpointFailure.Cancelled,
                "The run was cancelled.",
                Elapsed(started));
        }
        catch (OperationCanceledException)
        {
            return ScoringEndpointResult.Failed(
                ScoringEndpointFailure.TimedOut,
                $"{target.Host} did not answer within {current.TimeoutSeconds} seconds. "
                    + "Raise the timeout, or ask for fewer rankings.",
                Elapsed(started));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Scoring endpoint {Endpoint} could not be reached", target);

            return ScoringEndpointResult.Failed(
                ScoringEndpointFailure.Unreachable,
                $"Nothing answered at {target.Host}:{target.Port}. Is your model server running?",
                Elapsed(started));
        }
    }

    /// <summary>Reads the completion envelope, and nothing about the ranking inside it.</summary>
    /// <remarks>
    /// Whether the content is a ranking is <see cref="ScoringResponseParser"/>'s
    /// question. What is read here is the API's own envelope: which model answered, and
    /// whether it was allowed to finish.
    /// </remarks>
    private ScoringEndpointResult ReadCompletion(string body, TimeSpan duration, Uri target)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return ScoringEndpointResult.Failed(
                ScoringEndpointFailure.Unreadable,
                $"{target.Host} answered with something that is not a chat completion.",
                duration,
                Trim(body));
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return ScoringEndpointResult.Failed(
                    ScoringEndpointFailure.Unreadable,
                    $"{target.Host} answered without any choices in it.",
                    duration,
                    Trim(body));
            }

            var choice = choices[0];

            var reply = choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            // Read off the response rather than inferred from the shape of the broken
            // JSON. Every server in scope reports it, and it is the difference between
            // "your model misbehaved" and "your model ran out of room", which is a
            // failure with a fix the user already has a setting for.
            var finish = choice.TryGetProperty("finish_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;

            var model = root.TryGetProperty("model", out var named) && named.ValueKind == JsonValueKind.String
                ? named.GetString()
                : options.CurrentValue.Model;

            if (string.IsNullOrWhiteSpace(reply))
            {
                return ScoringEndpointResult.Failed(
                    ScoringEndpointFailure.Unreadable,
                    $"{target.Host} answered with an empty message.",
                    duration,
                    Trim(body));
            }

            if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
            {
                return ScoringEndpointResult.Failed(
                    ScoringEndpointFailure.Truncated,
                    "Your model ran out of room part-way through the reply. "
                        + "Ask for fewer rankings, or raise your server's output limit.",
                    duration,
                    Trim(reply));
            }

            logger.LogInformation(
                "Scoring endpoint {Endpoint} answered in {Seconds:0.0}s using {Model}",
                target,
                duration.TotalSeconds,
                model);

            return ScoringEndpointResult.Success(reply, model, duration);
        }
    }

    private static string Body(ScoringOptions current, string prompt, string payload, int maxTokens)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Whatever the operator called it. An empty name is left out entirely
            // rather than sent blank, because a server with one model loaded accepts
            // the request without it and would reject "".
            if (!string.IsNullOrWhiteSpace(current.Model))
            {
                writer.WriteString("model", current.Model);
            }

            writer.WriteNumber("temperature", Temperature);
            writer.WriteNumber("max_tokens", maxTokens);
            writer.WriteBoolean("stream", false);

            writer.WriteStartArray("messages");

            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", prompt);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", payload);
            writer.WriteEndObject();

            writer.WriteEndArray();

            if (current.UseStructuredOutput)
            {
                // json_schema rather than json_object, which is a correction rather
                // than a preference. This sent json_object on the grounds that it was
                // "the weakest form every server in scope understands" — and LM Studio,
                // the most likely target of all, answers that with
                //
                //   400: 'response_format.type' must be 'json_schema' or 'text'
                //
                // so the compatibility argument pointed the other way the whole time.
                // The schema is also the stronger request: a server that converts it to
                // a grammar cannot emit a code fence at all, which turns D37's
                // unwrapping back into the fallback it was meant to be.
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_schema");

                writer.WriteStartObject("json_schema");
                writer.WriteString("name", ScoringResponseSchema.Name);

                // Written through the reader so the schema is parsed once here rather
                // than pasted as a string a server would have to accept as text.
                using (var schema = JsonDocument.Parse(ScoringResponseSchema.Json))
                {
                    writer.WritePropertyName("schema");
                    schema.RootElement.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// A next step, when the server's own complaint names something AniQueue chose.
    /// </summary>
    /// <remarks>
    /// Reading the error rather than guessing at it: the only case handled is a server
    /// objecting to <c>response_format</c>, which is a field this application decided
    /// to send and can be told to stop sending. Everything else is left as the
    /// server's own words, because a hint about something we did not choose would be
    /// speculation dressed as help.
    ///
    /// This exists because the field's first real outing was rejected — LM Studio
    /// requires <c>json_schema</c> and refuses <c>json_object</c> — and a rejection
    /// naming a setting the user cannot connect to a checkbox is a dead end.
    /// </remarks>
    /// <summary>
    /// Recognises a request that did not fit, and says so in the server's own numbers.
    /// </summary>
    /// <remarks>
    /// Matched on the message text rather than on a field, because the shape of the
    /// error is the server's business and differs between them — LM Studio wraps
    /// llama.cpp's, and what reaches this client is the wrapping. The words survive
    /// the wrapping; the structure does not.
    ///
    /// The two numbers are read out of it where they are there, because "13,782
    /// against 8,192" tells somebody how far over they are and therefore which of the
    /// three remedies is likely to be enough. Without them the message is still true,
    /// just less useful, so their absence is not a reason to fall back to a worse
    /// error.
    /// </remarks>
    private static string? TooLarge(string? body)
    {
        if (body is null
            || (!body.Contains("context size", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("context length", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("exceed_context_size", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var sizes = ContextSizes().Matches(body);

        var measured = sizes.Count >= 2
            ? $" It needed {sizes[0].Groups[1].Value} tokens and the model has room for {sizes[1].Groups[1].Value}."
            : string.Empty;

        return "Your request was too big for the model to read." + measured
            + " Send fewer scored titles as history, rank fewer titles at once, "
            + "or give the model a larger context window.";
    }

    [GeneratedRegex(@"\((\d[\d,]*) tokens\)", RegexOptions.IgnoreCase)]
    private static partial Regex ContextSizes();

    private static string Hint(ScoringOptions current, string? body) =>
        current.UseStructuredOutput
        && body?.Contains("response_format", StringComparison.OrdinalIgnoreCase) == true
            ? " Your server will not accept the JSON format AniQueue asked for — "
                + "turn off \"Ask the server for JSON only\" and try again."
            : string.Empty;

    private TimeSpan Elapsed(long started) => _time.GetElapsedTime(started);

    private static string? Trim(string? body) =>
        string.IsNullOrEmpty(body)
            ? null
            : body.Length <= MaxDiagnosticLength
                ? body
                : body[..MaxDiagnosticLength] + "…";
}

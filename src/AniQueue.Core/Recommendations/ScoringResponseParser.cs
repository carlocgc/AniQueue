using System.Globalization;
using System.Text.Json;

namespace AniQueue.Core.Recommendations;

/// <summary>Bounds on what a response is allowed to consume.</summary>
/// <remarks>
/// A reply is untrusted input whether a person pasted it or Phase 8's endpoint
/// returned it, and an unbounded parse is a denial of service on a machine that is
/// also somebody's media server. The same argument <see cref="Import.ImportLimits"/>
/// makes, at a smaller scale: a ranking is bounded by the backlog that produced it.
/// </remarks>
public sealed record ScoringLimits
{
    public static ScoringLimits Default { get; } = new();

    /// <summary>4 MB. A ranking of fifty thousand titles with reasons is smaller than this.</summary>
    public int MaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>More results than the library could possibly hold means the reply is not about it.</summary>
    public int MaxResults { get; init; } = 50_000;

    /// <summary>Reasons longer than this are truncated, not rejected.</summary>
    public int MaxReasonLength { get; init; } = 500;
}

/// <summary>What reading a response produced: a ranking, and everything wrong with it.</summary>
public sealed record ScoringParseResult
{
    public ScoringResponse? Response { get; init; }

    public IReadOnlyList<ScoringProblem> Problems { get; init; } = [];

    public bool HasErrors => Problems.Any(p => p.Severity == ScoringSeverity.Error);

    public static ScoringParseResult Rejected(string message) =>
        new() { Problems = [ScoringProblem.Error(message)] };
}

/// <summary>
/// Reads the JSON a model returned, and refuses to guess.
/// </summary>
/// <remarks>
/// Read defensively through <see cref="JsonDocument"/> rather than deserialised
/// into the records, the same way <see cref="Import.AniListJsonParser"/> reads an
/// external response, and for a stronger reason here: §6 forbids executing or
/// evaluating AI content, and D31 makes the reply data that fails rather than data
/// that gets repaired. Deserialisation would silently accept a missing field as a
/// default — a predicted score of 0, a confidence of 0 — and write it to the database as
/// though the model had said it.
///
/// Nothing is inferred from a malformed reply. A response wrapped in prose, fenced
/// in markdown, or missing a required field is reported with what was wrong, and
/// the user tries again or edits it by hand. That is a worse experience than
/// guessing exactly once, and a better one every time after that, because a score
/// nobody can account for is the black box this workflow exists to avoid.
/// </remarks>
public sealed class ScoringResponseParser(ScoringLimits? limits = null)
{
    private readonly ScoringLimits _limits = limits ?? ScoringLimits.Default;

    public ScoringParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return ScoringParseResult.Rejected("There is nothing to read.");
        }

        // Measured in bytes rather than characters because the limit exists to bound
        // memory, and a reply full of native titles costs three bytes a character.
        var size = System.Text.Encoding.UTF8.GetByteCount(json);
        if (size > _limits.MaxBytes)
        {
            return ScoringParseResult.Rejected(
                $"The response is larger than the {_limits.MaxBytes / (1024 * 1024)} MB limit.");
        }

        JsonDocument document;
        ScoringProblem? unwrapped = null;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Not JSON as it stands, which is the ordinary case rather than the sad one:
            // a small model told to return only JSON very often returns it inside a
            // markdown fence, or after a sentence of introduction. So look for the
            // answer inside the text before giving up (D37).
            if (!TryUnwrap(json, out document!, out unwrapped))
            {
                // The original message, not one about unwrapping. It carries the line
                // and position, which is the only thing that makes a hand-edit
                // practical, and the reply is far more often malformed than wrapped.
                return ScoringParseResult.Rejected($"The response is not valid JSON: {ex.Message}");
            }
        }

        using (document)
        {
            var result = Read(document.RootElement);

            // First, because it is the reason everything below it is about a fragment
            // of what was pasted rather than the whole of it.
            return unwrapped is null
                ? result
                : result with { Problems = [unwrapped, .. result.Problems] };
        }
    }

    /// <summary>
    /// The most starting positions to try before giving up on a reply.
    /// </summary>
    /// <remarks>
    /// Every unmatched brace costs an attempted parse, so a pathological input — four
    /// megabytes of <c>{</c> — would otherwise be quadratic on a machine that is also
    /// somebody's media server. A real reply needs a handful of attempts.
    /// </remarks>
    private const int MaxUnwrapAttempts = 10_000;

    /// <summary>
    /// Finds a ranking inside text that is not JSON, and reports what it ignored.
    /// </summary>
    /// <remarks>
    /// <b>This unwraps; it never reconstructs (D37).</b> Each <c>{</c> is offered to
    /// <see cref="Utf8JsonReader"/>, which either reads one complete value from it or
    /// does not — so what comes out is bytes the model actually emitted, parsed by the
    /// same reader that would have parsed the whole reply. Nothing is repaired, no
    /// braces are counted by hand, and a <c>{</c> inside a title cannot start a
    /// candidate because the reader is inside a string when it reaches it.
    ///
    /// <b>What makes a candidate ours is a <c>results</c> array</b>, which is the same
    /// question <see cref="Read"/> asks of a reply that arrived clean. D37 said the
    /// envelope, and the envelope will not do: <see cref="ReadEnvelope"/> deliberately
    /// tolerates its absence, because models return the array reliably and the wrapper
    /// around it unreliably — so requiring it here would reject exactly the replies 7a
    /// went out of its way to accept.
    ///
    /// <b>The last match wins.</b> The prompt shows a worked example carrying this
    /// shape, so a model that restates the question before answering it produces two
    /// candidates; a model that thinks aloud does the same. In both, the answer is the
    /// one at the end.
    ///
    /// A matched value is skipped past rather than descended into, so a ranking nested
    /// inside some other object is not found. That is deliberate: reaching into a
    /// structure to pull out the part that looks right is the guessing this is supposed
    /// not to do.
    /// </remarks>
    private static bool TryUnwrap(string text, out JsonDocument? document, out ScoringProblem? note)
    {
        document = null;
        note = null;

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        JsonDocument? found = null;
        var start = 0;
        var length = 0;
        var matches = 0;
        var attempts = 0;

        for (var i = 0; i < bytes.Length;)
        {
            if (bytes[i] != (byte)'{')
            {
                i++;
                continue;
            }

            if (++attempts > MaxUnwrapAttempts)
            {
                break;
            }

            if (!TryReadValue(bytes, i, out var candidate, out var consumed))
            {
                i++;
                continue;
            }

            if (HasResults(candidate.RootElement))
            {
                found?.Dispose();
                found = candidate;
                start = i;
                length = consumed;
                matches++;
            }
            else
            {
                candidate.Dispose();
            }

            i += consumed;
        }

        if (found is null)
        {
            return false;
        }

        document = found;

        // Counted in characters because that is what the person looking at the reply on
        // screen is counting, and reported at all because a score nobody can account
        // for is what this pipeline exists to avoid: the preview says what was thrown
        // away, so a ranking read out of a mess is still one the user can audit.
        var ignored = text.Length - System.Text.Encoding.UTF8.GetString(bytes, start, length).Length;

        var message = matches > 1
            ? $"The reply had text around the ranking. {ignored} characters were ignored, "
                + $"including {matches - 1} earlier block(s) that also looked like a ranking."
            : $"The reply had text around the ranking. {ignored} characters were ignored.";

        note = ScoringProblem.Warning(message);

        return true;
    }

    /// <summary>Reads one complete JSON value beginning at <paramref name="start"/>.</summary>
    private static bool TryReadValue(byte[] bytes, int start, out JsonDocument document, out int consumed)
    {
        document = null!;
        consumed = 0;

        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(start), isFinalBlock: true, state: default);

            // Read the opening token, then skip the whole value it begins. BytesConsumed
            // is then exactly the extent of that value, which is what makes this a
            // measurement rather than a guess.
            if (!reader.Read() || !reader.TrySkip())
            {
                return false;
            }

            consumed = (int)reader.BytesConsumed;
            document = JsonDocument.Parse(bytes.AsMemory(start, consumed));

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasResults(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("results", out var results)
        && results.ValueKind == JsonValueKind.Array;

    private ScoringParseResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ScoringParseResult.Rejected(
                "The response must be a JSON object with a \"results\" array. "
                + $"This is {Describe(root.ValueKind)}.");
        }

        var problems = new List<ScoringProblem>();

        var library = ReadEnvelope(root, problems);

        if (!root.TryGetProperty("results", out var results))
        {
            problems.Add(ScoringProblem.Error("The response has no \"results\" array."));
            return new ScoringParseResult { Problems = problems };
        }

        if (results.ValueKind != JsonValueKind.Array)
        {
            problems.Add(ScoringProblem.Error(
                $"\"results\" must be an array. It is {Describe(results.ValueKind)}."));
            return new ScoringParseResult { Problems = problems };
        }

        var count = results.GetArrayLength();

        if (count == 0)
        {
            problems.Add(ScoringProblem.Error("The response ranked nothing."));
            return new ScoringParseResult { Problems = problems };
        }

        if (count > _limits.MaxResults)
        {
            problems.Add(ScoringProblem.Error(
                $"The response has {count} results, which is more than the {_limits.MaxResults} allowed."));
            return new ScoringParseResult { Problems = problems };
        }

        var parsed = new List<ScoringResult>(count);
        var seenIds = new HashSet<int>();
        var position = 0;

        foreach (var element in results.EnumerateArray())
        {
            position++;

            var result = ReadResult(element, position, problems);
            if (result is null)
            {
                continue;
            }

            // Checked here rather than after the loop so the message can name where the
            // second one was. A title scored twice is an error because there is no
            // reading of two different scores for one title that is safe to pick.
            //
            // The companion check — two titles claiming one rank — went with the field
            // in D43, along with the gap warning beside it. Both existed to police a
            // numbering nothing asks for or stores any more.
            if (!seenIds.Add(result.Id))
            {
                problems.Add(ScoringProblem.Error(
                    $"Result {position}: title {result.Id} was scored more than once."));
                continue;
            }

            parsed.Add(result);
        }

        return new ScoringParseResult
        {
            // Held even when there are errors, so a preview can still show what was
            // read beside what was wrong with it. Nothing downstream may apply it
            // while HasErrors is true, and IRecommendationService is where that is
            // enforced.
            // Ordered by the score, descending, because that is the only number the
            // model is now asked to produce — so a preview reads top-down in the order
            // the backlog will show once it is applied (D43). Until then this was the
            // model's own numbering, which is exactly the field that went.
            Response = new ScoringResponse
            {
                Library = library,
                Results = parsed.OrderByDescending(r => r.PredictedScore).ToList()
            },
            Problems = problems
        };
    }

    /// <summary>
    /// Checks the envelope, and is lenient about exactly one thing: its absence.
    /// </summary>
    /// <remarks>
    /// A model asked for JSON reliably returns the array it was asked for and
    /// unreliably returns the wrapper around it, so requiring the envelope would
    /// reject correct rankings for a field that carries no ranking. What it is
    /// strict about is an envelope that is present and <i>wrong</i>: a format name
    /// from some other document, or a version this build does not know how to read,
    /// both mean the reply is not an answer to this request.
    /// </remarks>
    private static string? ReadEnvelope(JsonElement root, List<ScoringProblem> problems)
    {
        if (!root.TryGetProperty("aniqueue", out var envelope) ||
            envelope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (envelope.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String)
        {
            var name = format.GetString();
            if (!string.Equals(name, ScoringResponse.ResponseFormat, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(ScoringProblem.Error(
                    $"The response says it is \"{name}\", not \"{ScoringResponse.ResponseFormat}\"."));
            }
        }

        if (envelope.TryGetProperty("version", out var version) &&
            version.ValueKind == JsonValueKind.Number &&
            version.TryGetInt32(out var number) &&
            number != ScoringRequest.CurrentVersion)
        {
            problems.Add(ScoringProblem.Error(
                $"The response is version {number}; this build reads version {ScoringRequest.CurrentVersion}."));
        }

        // Returned rather than judged (D50). A key that is present and belongs to
        // another library is the one thing here that cannot be decided without the
        // database, so this reads it and stops. A key of the wrong *shape* is not
        // rejected either: whatever it is, it either matches this library's key or it
        // does not, and "does not" is already the answer.
        return envelope.TryGetProperty("library", out var library) &&
            library.ValueKind == JsonValueKind.String
                ? library.GetString()
                : null;
    }

    private ScoringResult? ReadResult(JsonElement element, int position, List<ScoringProblem> problems)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(ScoringProblem.Error(
                $"Result {position} is {Describe(element.ValueKind)}, not an object."));
            return null;
        }

        var id = ReadInt(element, "id", position, problems);
        var predictedScore = ReadDouble(element, "predictedScore", position, problems);
        var confidence = ReadDouble(element, "confidence", position, problems);

        // A "rank" alongside these is simply not read (D43). Not an error: a model
        // reproducing a shape from training has still answered the question, and
        // failing a batch over a field nothing consumes would spend the sweep's error
        // budget on the model being old-fashioned.
        if (id is null || predictedScore is null || confidence is null)
        {
            return null;
        }

        // The scale is the request's, and every parser normalises into it, so this is
        // a fixed range rather than one carried back from the reply — a response that
        // could nominate its own bounds could put any number in range.
        if (!ScoringScale.Default.Contains(predictedScore.Value))
        {
            problems.Add(ScoringProblem.Error(
                $"Result {position}: predicted score {Format(predictedScore.Value)} is outside "
                + $"{ScoringScale.Default.Min}–{ScoringScale.Default.Max}."));
            return null;
        }

        if (confidence is < 0 or > 1)
        {
            problems.Add(ScoringProblem.Error(
                $"Result {position}: confidence {Format(confidence.Value)} is outside 0–1."));
            return null;
        }

        return new ScoringResult
        {
            Id = id.Value,
            PredictedScore = predictedScore.Value,
            Confidence = confidence.Value,
            Reason = ReadReason(element, position, problems)
        };
    }

    private string? ReadReason(JsonElement element, int position, List<ScoringProblem> problems)
    {
        if (!element.TryGetProperty("reason", out var reason) ||
            reason.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = reason.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        if (text.Length <= _limits.MaxReasonLength)
        {
            return text;
        }

        problems.Add(ScoringProblem.Warning(
            $"Result {position}: the reason was longer than {_limits.MaxReasonLength} characters and was shortened."));

        return text[.._limits.MaxReasonLength];
    }

    // WarnOnRankGaps stood here. It reported a numbering that started above 1 or
    // ended below the count, on the reasoning that the usual cause is a model
    // dropping entries it had already numbered. The signal was real and D43 removed
    // what it read: with no rank in the reply there is no sequence to find a hole in,
    // and a short reply is already reported against ExpectedCount by ScoringPreview,
    // which measures it against what was asked for rather than against the model's
    // own count of itself.

    private static int? ReadInt(JsonElement element, string name, int position, List<ScoringProblem> problems)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            problems.Add(ScoringProblem.Error($"Result {position} has no \"{name}\"."));
            return null;
        }

        // Numbers only. A model that returns "id": "412" has returned a string, and
        // accepting it here is the first step towards accepting "id": "unknown".
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            problems.Add(ScoringProblem.Error(
                $"Result {position}: \"{name}\" must be a whole number, and is {Describe(value.ValueKind)}."));
            return null;
        }

        return number;
    }

    private static double? ReadDouble(JsonElement element, string name, int position, List<ScoringProblem> problems)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            problems.Add(ScoringProblem.Error($"Result {position} has no \"{name}\"."));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            problems.Add(ScoringProblem.Error(
                $"Result {position}: \"{name}\" must be a number, and is {Describe(value.ValueKind)}."));
            return null;
        }

        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            problems.Add(ScoringProblem.Error($"Result {position}: \"{name}\" is not a finite number."));
            return null;
        }

        return number;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Null => "null",
        _ => "missing"
    };
}

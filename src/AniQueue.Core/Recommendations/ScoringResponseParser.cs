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
/// default — a rank of 0, a confidence of 0 — and write it to the database as
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

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // The message carries the line and position, which is the only thing that
            // makes a hand-edit practical.
            return ScoringParseResult.Rejected($"The response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            return Read(document.RootElement);
        }
    }

    private ScoringParseResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ScoringParseResult.Rejected(
                "The response must be a JSON object with a \"results\" array. "
                + $"This is {Describe(root.ValueKind)}.");
        }

        var problems = new List<ScoringProblem>();

        ReadEnvelope(root, problems);

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
        var seenRanks = new HashSet<int>();
        var position = 0;

        foreach (var element in results.EnumerateArray())
        {
            position++;

            var result = ReadResult(element, position, problems);
            if (result is null)
            {
                continue;
            }

            // Duplicates are checked here rather than after the loop so the message can
            // name where the second one was. Both are errors: a title ranked twice and
            // two titles claiming one rank are each a ranking that does not describe an
            // order, and there is no reading of either that is safe to pick.
            if (!seenIds.Add(result.Id))
            {
                problems.Add(ScoringProblem.Error(
                    $"Result {position}: title {result.Id} was ranked more than once."));
                continue;
            }

            if (!seenRanks.Add(result.Rank))
            {
                problems.Add(ScoringProblem.Error(
                    $"Result {position}: rank {result.Rank} was used more than once."));
                continue;
            }

            parsed.Add(result);
        }

        WarnOnRankGaps(parsed, problems);

        return new ScoringParseResult
        {
            // Held even when there are errors, so a preview can still show what was
            // read beside what was wrong with it. Nothing downstream may apply it
            // while HasErrors is true, and IRecommendationService is where that is
            // enforced.
            Response = new ScoringResponse { Results = parsed.OrderBy(r => r.Rank).ToList() },
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
    private static void ReadEnvelope(JsonElement root, List<ScoringProblem> problems)
    {
        if (!root.TryGetProperty("aniqueue", out var envelope) ||
            envelope.ValueKind != JsonValueKind.Object)
        {
            return;
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
        var rank = ReadInt(element, "rank", position, problems);
        var predictedScore = ReadDouble(element, "predictedScore", position, problems);
        var confidence = ReadDouble(element, "confidence", position, problems);

        if (id is null || rank is null || predictedScore is null || confidence is null)
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

        if (rank < 1)
        {
            problems.Add(ScoringProblem.Error($"Result {position}: rank {rank} is not 1 or greater."));
            return null;
        }

        return new ScoringResult
        {
            Id = id.Value,
            Rank = rank.Value,
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

    /// <summary>
    /// A ranking that skips ranks still states an order, so this is reported rather
    /// than refused — but it is reported, because the usual cause is a model that
    /// dropped entries it had already numbered, and that is worth knowing before
    /// reading the result as complete.
    /// </summary>
    private static void WarnOnRankGaps(List<ScoringResult> parsed, List<ScoringProblem> problems)
    {
        if (parsed.Count == 0)
        {
            return;
        }

        var ranks = parsed.Select(r => r.Rank).Order().ToList();

        if (ranks[0] != 1 || ranks[^1] != parsed.Count)
        {
            problems.Add(ScoringProblem.Warning(
                $"The ranks run from {ranks[0]} to {ranks[^1]} across {parsed.Count} results, "
                + "so some are missing. The order is still used as given."));
        }
    }

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

using System.Globalization;
using System.Text;

namespace AniQueue.Core.Recommendations;

/// <summary>
/// Writes the instructions that travel with a request.
/// </summary>
/// <remarks>
/// In Core rather than on the page that shows it, because Phase 8 sends exactly
/// this text to a configured endpoint and a prompt that lived in a Razor component
/// would either be duplicated or reached for from the wrong layer. It is part of
/// the contract, not part of the presentation: what a model is told to return and
/// what <see cref="ScoringResponseParser"/> accepts are two statements of one
/// thing, and keeping them in the same folder is the cheapest way to notice when
/// they stop agreeing.
///
/// <b>Written for a small model.</b> The target is something self-hosted, so the
/// instructions are short, ordered with the output format last — which is the part
/// most likely to survive a truncated context — and state the failure explicitly
/// rather than politely. "Return only JSON" outperforms "please try to return
/// JSON" on a 7B model by a wide margin, and prose around the object is the single
/// most common reason a reply is rejected.
/// </remarks>
public static class ScoringPromptBuilder
{
    /// <summary>The instruction text for a request, without the payload itself.</summary>
    public static string Build(ScoringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scale = request.Scale;
        var prompt = new StringBuilder();

        prompt.AppendLine(
            "You are ranking one person's anime backlog. Everything you need is in the JSON below.");
        prompt.AppendLine();

        prompt.AppendLine("What the JSON contains:");
        prompt.AppendLine(
            CultureInfo.InvariantCulture,
            $"- \"history\": titles this person has finished and rated {scale.Min}–{scale.Max}. This is their taste. Use it.");

        if (request.IsHistoryCapped)
        {
            prompt.AppendLine(
                CultureInfo.InvariantCulture,
                $"  It is a sample of their {request.HistoryAvailable} rated titles, most recently finished first.");
        }

        prompt.AppendLine(
            CultureInfo.InvariantCulture,
            $"- \"candidates\": {request.Candidates.Count} titles they have not watched yet. Rank these.");
        prompt.AppendLine();

        prompt.AppendLine("How to rank:");
        prompt.AppendLine("- Predict what THIS person would rate each candidate, not how well regarded it is.");
        prompt.AppendLine("- A title similar to something they rated low should rank low, however popular it is.");
        prompt.AppendLine("- Say why in one short sentence, referring to their history where you can.");
        prompt.AppendLine("- If you do not recognise a title, give it a low confidence rather than a guessed score.");
        prompt.AppendLine();

        prompt.AppendLine("Return this exact shape, and nothing else:");
        prompt.AppendLine();
        prompt.AppendLine(Schema(scale));
        prompt.AppendLine();

        prompt.AppendLine("Rules for the reply:");
        prompt.AppendLine("- Output JSON only. No explanation before it, no code fence around it.");
        prompt.AppendLine("- \"id\" must be copied exactly from the candidate. Never invent one.");
        prompt.AppendLine(
            CultureInfo.InvariantCulture,
            $"- \"rank\" starts at 1 and each is used once. \"predictedScore\" is {scale.Min}–{scale.Max}. \"confidence\" is 0–1.");
        prompt.AppendLine("- Rank every candidate. If you cannot rank them all, rank as many as you can and stop.");

        return prompt.ToString().TrimEnd();
    }

    /// <summary>
    /// The response shape, as an example rather than as a specification.
    /// </summary>
    /// <remarks>
    /// A worked example rather than JSON Schema, deliberately. A small model
    /// reproduces a shape it has seen far more reliably than it satisfies a
    /// description of one, and the strictness that matters lives in the parser
    /// where it can be tested — an instruction is a request, not a guarantee, and
    /// treating it as one is how invalid output reaches the database.
    /// </remarks>
    public static string Schema(ScoringScale scale) => $$"""
        {
          "aniqueue": { "format": "{{ScoringResponse.ResponseFormat}}", "version": {{ScoringRequest.CurrentVersion}} },
          "results": [
            { "id": 412, "rank": 1, "predictedScore": {{scale.Max - 1}}.5, "confidence": 0.8, "reason": "Same director as one you rated 9." },
            { "id": 98, "rank": 2, "predictedScore": 7.0, "confidence": 0.6, "reason": "Close to a genre you rate well." }
          ]
        }
        """;
}

using System.Globalization;
using System.Text;

namespace AniQueue.Core.Recommendations;

/// <summary>
/// Writes the instructions that travel with a request.
/// </summary>
/// <remarks>
/// In Core rather than on the page that shows it, because a configured endpoint
/// sends exactly this text and a prompt living in a Razor component would either be
/// duplicated or reached for from the wrong layer. It is part of
/// the contract, not part of the presentation: what a model is told to return and
/// what <see cref="ScoringResponseParser"/> accepts are two statements of one
/// thing, and keeping them in the same folder is the cheapest way to notice when
/// they stop agreeing.
///
/// The example teaches as much as the rules do, so its reasons carry no numbers: a
/// number in a reason beside a score gets reproduced as a rating the model invented.
/// One of them shows what to say when there is no comparison to make.
///
/// No real title appears in the example either, which is why the rules ask for a
/// pattern as well as for a name. Putting a concrete title in front of a model whose
/// observed failure is copying example content produces a plausible lie that nothing
/// downstream can catch.
///
/// Written for a small model. The target is something self-hosted, so the
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
        // Two lines where there was one, and both are answers to output seen from real
        // models rather than to anything imagined. "Referring to their history where
        // you can" is what a model turns into an invented rating when nothing in the
        // history is close: qwen3-vl-8b returned "You rated 'Haite Kudasai,
        // Takamine-san' 6.0." about an unwatched candidate, with 6.0 being its own
        // predictedScore; gpt-oss-20b wrote "Sci-fi thriller like Psycho-Pass you rated
        // 7." about Psycho-Pass itself. Both named the candidate as something the user
        // had rated, and both used their own score as the rating.
        prompt.AppendLine(
            "- Say why in one short sentence, grounded in \"history\" — name a title from it, "
                + "or describe a pattern across it. Everything in \"candidates\" is unwatched: "
                + "never say they rated one.");
        prompt.AppendLine(
            "- Never put your own predictedScore in the reason. A number there is one of "
                + "THEIR ratings, copied from \"history\", or there is no number.");
        prompt.AppendLine(
            "- If nothing in their history is close, say so rather than forcing a comparison.");
        prompt.AppendLine("- If you do not recognise a title, give it a low confidence rather than a guessed score.");

        // Stated as "consider all, return some", in that order and in those words.
        // A model told only "return 50" tends to read the first fifty candidates and
        // rank those, which is a different and much worse answer than the best fifty
        // — and one that looks identical in the reply.
        if (request.IsRankingLimited)
        {
            prompt.AppendLine();
            prompt.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Weigh ALL {request.Candidates.Count} candidates, then return only the best "
                    + $"{request.ExpectedResults}. Do not rank the first {request.ExpectedResults} you see.");
            prompt.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Leave the other {request.Candidates.Count - request.ExpectedResults} out of the reply entirely.");
        }

        prompt.AppendLine();

        prompt.AppendLine("Return this exact shape, and nothing else:");
        prompt.AppendLine();
        prompt.AppendLine(Schema(scale, request.Library));
        prompt.AppendLine();

        prompt.AppendLine("Rules for the reply:");
        prompt.AppendLine("- Output JSON only. No explanation before it, no code fence around it.");
        prompt.AppendLine("- \"id\" must be copied exactly from the candidate. Never invent one.");

        // Asked for plainly, because the whole value of the key is that it travels
        // with a reply a person may keep and paste back months later. A model
        // that ignores this produces a reply AniQueue reads exactly as it read replies
        // before the key existed, so the instruction is worth making and not worth
        // insisting on.
        if (!string.IsNullOrEmpty(request.Library))
        {
            prompt.AppendLine(
                "- Copy \"library\" into the reply's \"aniqueue\" object exactly as the request states it.");
        }

        prompt.AppendLine(
            CultureInfo.InvariantCulture,
            $"- \"predictedScore\" is {scale.Min}–{scale.Max}. \"confidence\" is 0–1.");

        // No rule about "rank", because nothing asks for one any more. Asking
        // for a placement alongside a score got the score derived from the placement
        // — observed output ranged from an integer staircase locked to position to
        // genuinely independent scoring — and the score is the half that is stored.
        if (request.IsRankingLimited)
        {
            prompt.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Return exactly {request.ExpectedResults} results.");
        }
        else
        {
            // Without a limit, permission to stop early is what keeps a small model's
            // short answer a usable ranking rather than a rejected one.
            prompt.AppendLine(
                "- Rank every candidate. If you cannot rank them all, rank as many as you can and stop.");
        }

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
    public static string Schema(ScoringScale scale, string? library = null) => $$"""
        {
          "aniqueue": { "format": "{{ScoringResponse.ResponseFormat}}", "version": {{ScoringRequest.CurrentVersion}}{{LibraryField(library)}} },
          "results": [
            { "id": 412, "predictedScore": {{scale.Max - 1}}.5, "confidence": 0.8, "reason": "Same studio as several of your highest-rated titles." },
            { "id": 98, "predictedScore": 7.0, "confidence": 0.6, "reason": "Nothing close in your history; ranked on genre alone." }
          ]
        }
        """;

    /// <summary>
    /// The library key as it appears inside the example envelope, or nothing at all
    /// when the request carries none.
    /// </summary>
    /// <remarks>
    /// Shown in the worked example rather than described in a rule alone, for the
    /// reason <see cref="Schema"/> exists at all: a model reproduces a shape it has
    /// seen more reliably than it follows a sentence about one.
    ///
    /// The example carries this request's own key rather than a placeholder, and
    /// that is what makes it safe. The parser accepts a reply by finding the last object
    /// carrying a <c>results</c> array, and this example is itself such an object — a
    /// model that restates the question before answering it emits two. So a model that
    /// copies the envelope out of the example instead of out of the request still
    /// names the right library. A placeholder here would have made the example a
    /// source of convincingly wrong keys.
    /// </remarks>
    private static string LibraryField(string? library) =>
        string.IsNullOrEmpty(library) ? string.Empty : $", \"library\": \"{library}\"";
}

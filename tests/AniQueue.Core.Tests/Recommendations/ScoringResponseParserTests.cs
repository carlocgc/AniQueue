using System.Text.Json;
using AniQueue.Core.Recommendations;

namespace AniQueue.Core.Tests.Recommendations;

/// <summary>
/// The strict half of Phase 7's contract, tested without a database.
///
/// Every case here is one a self-hosted model actually produces. The split these
/// assert — errors reject the whole ranking, warnings do not — is the reason the
/// feature works on a model small enough to run at home, so the boundary between
/// the two is load-bearing rather than a matter of taste.
/// </summary>
public class ScoringResponseParserTests
{
    private static readonly ScoringResponseParser Parser = new();

    private static string Wrap(string results) =>
        $$"""
          {
            "aniqueue": { "format": "aniqueue-scoring-response", "version": 1 },
            "results": {{results}}
          }
          """;

    [Fact]
    public void Reads_a_well_formed_response()
    {
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 7, "predictedScore": 8.6, "confidence": 0.72, "reason": "Close to your top scores." },
              { "id": 9, "predictedScore": 7.1, "confidence": 0.5 }
            ]
            """));

        Assert.False(result.HasErrors);
        Assert.Empty(result.Problems);

        var results = result.Response!.Results;
        Assert.Equal(2, results.Count);
        Assert.Equal(7, results[0].Id);
        Assert.Equal("Close to your top scores.", results[0].Reason);
        Assert.Null(results[1].Reason);
    }

    [Fact]
    public void Orders_results_by_predicted_score_rather_than_by_position()
    {
        // A model may emit them in any order, and since D43 there is no rank stating
        // one. The score is the only number it is asked for, so ordering by it is what
        // makes a preview read in the order the backlog will once it is applied.
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 9, "predictedScore": 7.1, "confidence": 0.5 },
              { "id": 7, "predictedScore": 8.6, "confidence": 0.7 }
            ]
            """));

        Assert.False(result.HasErrors);
        Assert.Equal([7, 9], result.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public void A_rank_that_arrives_anyway_is_read_past_rather_than_refused()
    {
        // D43 took rank out of the request, not out of the world: a model repeating a
        // shape it saw in training still sends one. Failing the batch over a field
        // nothing reads would spend the sweep's error budget on the model being
        // old-fashioned — and here the rank contradicts the scores outright, so this
        // also pins down which of the two the parser believes.
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 9, "rank": 1, "predictedScore": 7.1, "confidence": 0.5 },
              { "id": 7, "rank": 2, "predictedScore": 8.6, "confidence": 0.7 }
            ]
            """));

        Assert.False(result.HasErrors);
        Assert.Empty(result.Problems);
        Assert.Equal([7, 9], result.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public void An_envelope_is_optional()
    {
        // Models reliably return the array and unreliably return the wrapper, so its
        // absence cannot be what rejects an otherwise correct ranking.
        var result = Parser.Parse(
            """
            { "results": [ { "id": 7, "predictedScore": 8.0, "confidence": 0.6 } ] }
            """);

        Assert.False(result.HasErrors);
        Assert.Single(result.Response!.Results);
    }

    [Fact]
    public void An_envelope_naming_another_format_is_an_error()
    {
        var result = Parser.Parse(
            """
            {
              "aniqueue": { "format": "aniqueue-scoring-request", "version": 1 },
              "results": [ { "id": 7, "predictedScore": 8.0, "confidence": 0.6 } ]
            }
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("aniqueue-scoring-request"));
    }

    [Fact]
    public void An_unreadable_version_is_an_error()
    {
        var result = Parser.Parse(
            """
            {
              "aniqueue": { "format": "aniqueue-scoring-response", "version": 2 },
              "results": [ { "id": 7, "predictedScore": 8.0, "confidence": 0.6 } ]
            }
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("version 2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_is_an_error(string json)
    {
        Assert.True(Parser.Parse(json).HasErrors);
    }

    [Fact]
    public void Prose_around_an_empty_ranking_is_unwrapped_and_then_still_refused()
    {
        // This asserted the opposite until D37: prose around the JSON was an error,
        // on the grounds that stripping a fence is the first step towards inferring
        // what a model meant. That held while a person was pasting the reply and could
        // delete the backticks; Phase 8 removed the person, and fencing is what small
        // models do most of the time — so the rule would have failed correct rankings
        // every night, forever, with nobody to fix them.
        //
        // What survives is the part that was actually load-bearing. Unwrapping changes
        // nothing about what is accepted: this reply is still refused, for the reason
        // it was always going to be refused, and the warning names what was discarded
        // so the refusal is about the ranking rather than about the backticks.
        var result = Parser.Parse(
            """
            Sure! Here is your ranking:
            ```json
            { "results": [] }
            ```
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("ranked nothing"));
        Assert.Contains(result.Problems, p => p.Severity == ScoringSeverity.Warning);
    }

    [Fact]
    public void A_bare_array_is_an_error_that_says_what_was_expected()
    {
        var result = Parser.Parse("""[ { "id": 7, "predictedScore": 8.0, "confidence": 0.6 } ]""");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("results"));
    }

    [Fact]
    public void A_missing_results_array_is_an_error()
    {
        Assert.True(Parser.Parse("""{ "ranking": [] }""").HasErrors);
    }

    [Fact]
    public void An_empty_ranking_is_an_error()
    {
        var result = Parser.Parse(Wrap("[]"));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("ranked nothing"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("predictedScore")]
    [InlineData("confidence")]
    public void Every_required_field_is_required(string omitted)
    {
        var fields = new Dictionary<string, string>
        {
            ["id"] = "7",
            ["predictedScore"] = "8.0",
            ["confidence"] = "0.6"
        };

        fields.Remove(omitted);

        var body = string.Join(", ", fields.Select(f => $"\"{f.Key}\": {f.Value}"));
        var result = Parser.Parse(Wrap($"[ {{ {body} }} ]"));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains(omitted));
    }

    [Fact]
    public void A_numeric_field_arriving_as_text_is_an_error()
    {
        // Deserialisation would coerce this. Reading it explicitly is the difference
        // between rejecting a wrong id and storing one.
        var result = Parser.Parse(Wrap(
            """[ { "id": "7", "predictedScore": 8.0, "confidence": 0.6 } ]"""));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("whole number"));
    }

    [Fact]
    public void A_title_scored_twice_is_an_error()
    {
        // Two different scores for one title, with no honest way to pick between them.
        // The companion case — two titles claiming one rank — was a test here until
        // D43 deleted the field, and it is not replaced: nothing else in the reply can
        // now contradict itself the way a numbering could.
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 7, "predictedScore": 8.0, "confidence": 0.6 },
              { "id": 7, "predictedScore": 7.0, "confidence": 0.6 }
            ]
            """));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("title 7 was scored more than once"));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(10.5)]
    [InlineData(-1)]
    public void A_predicted_score_off_the_scale_is_an_error(double score)
    {
        var result = Parser.Parse(Wrap(
            $$"""[ { "id": 7, "predictedScore": {{score}}, "confidence": 0.6 } ]"""));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("outside 1–10"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(72)]
    public void A_confidence_outside_zero_to_one_is_an_error(double confidence)
    {
        // 72 is the interesting one: a model asked for a percentage answers in
        // percent, which is a plausible-looking number in the wrong unit.
        var result = Parser.Parse(Wrap(
            $$"""[ { "id": 7, "predictedScore": 8.0, "confidence": {{confidence}} } ]"""));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("outside 0–1"));
    }

    // A_rank_below_one_is_an_error and A_gap_in_the_ranks_is_a_warning_and_still_applies
    // stood here. Both policed a numbering, and D43 removed the numbering; stripping
    // the rank out of their fixtures would have left two tests whose setup no longer
    // reaches the assertion they were written for, which is worse than not having them.
    // A reply that is short is still reported, but against ExpectedCount in
    // ScoringPreview rather than against the model's own count of itself.

    [Fact]
    public void An_overlong_reason_is_shortened_and_warned_about_rather_than_refused()
    {
        var reason = new string('a', 900);

        var result = Parser.Parse(Wrap(
            $$"""[ { "id": 7, "predictedScore": 8.0, "confidence": 0.6, "reason": "{{reason}}" } ]"""));

        Assert.False(result.HasErrors);
        Assert.Equal(500, result.Response!.Results[0].Reason!.Length);
        Assert.Contains(result.Problems, p => p.Severity == ScoringSeverity.Warning);
    }

    [Fact]
    public void A_response_larger_than_the_limit_is_refused_before_it_is_parsed()
    {
        var parser = new ScoringResponseParser(new ScoringLimits { MaxBytes = 64 });

        var result = parser.Parse(Wrap(
            """[ { "id": 7, "predictedScore": 8.0, "confidence": 0.6 } ]"""));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("larger than"));
    }

    [Fact]
    public void Errors_do_not_stop_the_rest_of_the_ranking_being_read()
    {
        // The preview has to show what was read beside what was wrong with it,
        // otherwise "one of your 182 results is malformed" is unactionable. Applying
        // it is refused elsewhere, by HasErrors.
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 7, "predictedScore": 8.0, "confidence": 0.6 },
              { "id": 9, "predictedScore": 99, "confidence": 0.6 },
              { "id": 11, "predictedScore": 6.0, "confidence": 0.6 }
            ]
            """));

        Assert.True(result.HasErrors);
        Assert.Equal([7, 11], result.Response!.Results.Select(r => r.Id));
    }

    // D37: a reply may be unwrapped, never reconstructed. Every case below is one a
    // self-hosted model produces routinely — and the phase that automated the carrying
    // is the phase these had to start passing, because the manual path had a person to
    // delete the backticks and a scheduled sweep has nobody.

    [Fact]
    public void A_ranking_inside_a_markdown_fence_is_read()
    {
        // The single most common reply shape from a small model, whatever the prompt
        // says. Rejecting it would have made the scheduled sweep fail every night on a
        // model that was working perfectly.
        var result = Parser.Parse(
            $"""
             ```json
             {Wrap("""[{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }]""")}
             ```
             """);

        Assert.False(result.HasErrors);
        Assert.Equal([7], result.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public void Prose_on_both_sides_of_the_ranking_is_ignored()
    {
        var result = Parser.Parse(
            $"""
             Sure! Here is the ranking you asked for:

             {Wrap("""[{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }]""")}

             Hope that helps! Let me know if you would like me to explain any of these.
             """);

        Assert.False(result.HasErrors);
        Assert.Single(result.Response!.Results);
    }

    [Fact]
    public void Unwrapping_says_what_it_ignored()
    {
        // The audit trail, and the reason unwrapping is not the black box D31 forbids:
        // the preview states what was discarded, so a ranking read out of a mess is
        // still one somebody can check.
        var result = Parser.Parse(
            $"""
             Here you go:
             {Wrap("""[{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }]""")}
             """);

        var note = Assert.Single(result.Problems, p => p.Severity == ScoringSeverity.Warning);

        Assert.Contains("ignored", note.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void A_clean_reply_is_not_reported_as_unwrapped()
    {
        // Nothing was thrown away, so nothing is said. A warning on every reply would
        // train the reader to ignore the one that matters.
        var result = Parser.Parse(Wrap(
            """[{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }]"""));

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void The_last_ranking_wins_when_a_model_echoes_the_example_first()
    {
        // Not hypothetical: the prompt contains a worked example carrying this exact
        // shape, so a model that restates the question before answering it emits two.
        // The answer follows the preamble, so the answer is the last one.
        var result = Parser.Parse(
            $"""
             You asked me to return this shape:
             {Wrap("""[{ "id": 412, "predictedScore": 9.5, "confidence": 0.8 }]""")}

             Here is my actual ranking:
             {Wrap("""[{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }]""")}
             """);

        Assert.Equal([7], result.Response!.Results.Select(r => r.Id));

        var note = Assert.Single(result.Problems, p => p.Severity == ScoringSeverity.Warning);
        Assert.Contains("earlier block", note.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_reasoning_block_before_the_answer_is_ignored()
    {
        var result = Parser.Parse(
            $"""
             <think>
             The user rated Gunbuster 10 and Najica 4, so they like dense sci-fi.
             I should rank the OVA highest.
             </think>
             {Wrap("""[{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }]""")}
             """);

        Assert.False(result.HasErrors);
        Assert.Equal([7], result.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public void An_envelope_free_ranking_is_still_found()
    {
        // The envelope is optional by design — ReadEnvelope tolerates its absence
        // because models return the array reliably and the wrapper unreliably. So the
        // thing that identifies a candidate is the results array, and a fenced reply
        // without an envelope has to be found exactly like one with it.
        var result = Parser.Parse(
            """
            ```
            { "results": [{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }] }
            ```
            """);

        Assert.False(result.HasErrors);
        Assert.Equal([7], result.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public void A_brace_inside_a_title_cannot_start_a_candidate()
    {
        // Utf8JsonReader is inside a string when it reaches that brace, which is the
        // whole reason the extent is measured by the reader rather than by counting
        // braces by hand.
        var result = Parser.Parse(
            """
            Here:
            { "results": [{ "id": 7, "predictedScore": 8.0, "confidence": 0.6,
                            "reason": "Like Re:Zero {Director's Cut}, but shorter." }] }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal("Like Re:Zero {Director's Cut}, but shorter.", result.Response!.Results.Single().Reason);
    }

    [Fact]
    public void Prose_with_no_ranking_in_it_is_still_refused()
    {
        // The floor. A model that answered in sentences has not answered, and inventing
        // a ranking from what it said is the guessing D31 exists to forbid.
        var result = Parser.Parse(
            "I would start with Hinamatsuri, then Dragon Maid. Lain is excellent but heavy.");

        Assert.True(result.HasErrors);
        Assert.Null(result.Response);
    }

    [Fact]
    public void An_object_that_is_not_a_ranking_is_not_mistaken_for_one()
    {
        // JSON-shaped is not the same as ours. Something has to identify a candidate,
        // and "it parsed" is not enough.
        var result = Parser.Parse(
            """
            ```json
            { "error": "context length exceeded", "code": 400 }
            ```
            """);

        Assert.True(result.HasErrors);
        Assert.Null(result.Response);
    }

    [Fact]
    public void A_ranking_nested_inside_another_object_is_not_dug_out()
    {
        // Reaching into a structure to pull out the part that looks right is
        // reconstruction rather than unwrapping, and the line has to be somewhere.
        // A model that wraps its answer has not produced the agreed shape.
        var result = Parser.Parse(
            """
            ```json
            { "output": { "results": [{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }] } }
            ```
            """);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Unwrapping_does_not_relax_anything_that_follows_it()
    {
        // The second floor: what is found is validated exactly as a clean reply is.
        var result = Parser.Parse(
            """
            Here you go:
            ```json
            { "results": [
                { "id": 7, "predictedScore": 8.0, "confidence": 0.6 },
                { "id": 7, "predictedScore": 7.0, "confidence": 0.6 }
            ] }
            ```
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("title 7 was scored more than once"));
    }

    [Fact]
    public void The_wire_schema_describes_what_the_parser_accepts()
    {
        // Three statements of one contract — the prompt's example, the schema a server
        // enforces, and what the parser will take. This is the cheapest guard on the
        // pair most likely to drift: a schema that forbids what the parser accepts
        // makes a server refuse good rankings, and one that permits what the parser
        // rejects wastes a ten-minute run.
        using var schema = JsonDocument.Parse(ScoringResponseSchema.Json);

        var item = schema.RootElement
            .GetProperty("properties").GetProperty("results")
            .GetProperty("items");

        var required = item.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        // The three the parser refuses a result without. "reason" is deliberately absent:
        // a model that omits it has still answered the question. "rank" was a fourth
        // until D43, and its absence from "properties" as well as from "required" is
        // what this asserts next — the schema is open, so a model that sends one anyway
        // is not refused by the server before the parser ever sees it.
        Assert.Equal(["id", "predictedScore", "confidence"], required);
        Assert.True(item.GetProperty("properties").TryGetProperty("reason", out _));
        Assert.False(item.GetProperty("properties").TryGetProperty("rank", out _));
        Assert.False(item.TryGetProperty("additionalProperties", out _));

        // And a reply built to this schema is one the parser reads without complaint.
        var result = new ScoringResponseParser().Parse(
            """{ "results": [{ "id": 7, "predictedScore": 8.0, "confidence": 0.6 }] }""");

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void The_schema_does_not_require_the_envelope_the_parser_tolerates_missing()
    {
        // Requiring it on the wire would make a server refuse replies AniQueue would
        // have accepted, which is a worse failure than the one it would prevent.
        using var schema = JsonDocument.Parse(ScoringResponseSchema.Json);

        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString());

        Assert.Equal(["results"], required);
    }

    [Fact]
    public void Reads_the_library_a_reply_names()
    {
        // Read here and judged by IRecommendationService, which is the only thing
        // holding the library it would have to be judged against (D50).
        var result = Parser.Parse(
            """
            {
              "aniqueue": { "format": "aniqueue-scoring-response", "version": 1, "library": "a1b2c3d4e5f6" },
              "results": [{ "id": 1, "predictedScore": 8.0, "confidence": 0.5 }]
            }
            """);

        Assert.Empty(result.Problems);
        Assert.Equal("a1b2c3d4e5f6", result.Response!.Library);
    }

    [Fact]
    public void A_reply_naming_no_library_is_still_a_reply()
    {
        // The leniency that makes the whole check safe to add: models drop the
        // envelope, and refusing a ranking over a field carrying no ranking would
        // cost the user everything and protect nothing.
        var result = Parser.Parse(Wrap("""[{ "id": 1, "predictedScore": 8.0, "confidence": 0.5 }]"""));

        Assert.Empty(result.Problems);
        Assert.Null(result.Response!.Library);
    }

    [Fact]
    public void A_library_key_of_any_shape_is_read_rather_than_judged()
    {
        // Deliberately not validated. Whatever it is, it either matches this
        // library's key or it does not, and "does not" is already the answer — so a
        // shape rule here would only add a second way to reject the same reply.
        var result = Parser.Parse(
            """
            {
              "aniqueue": { "format": "aniqueue-scoring-response", "version": 1, "library": "not a key" },
              "results": [{ "id": 1, "predictedScore": 8.0, "confidence": 0.5 }]
            }
            """);

        Assert.Empty(result.Problems);
        Assert.Equal("not a key", result.Response!.Library);
    }
}

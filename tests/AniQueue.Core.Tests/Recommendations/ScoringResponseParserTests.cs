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
              { "id": 7, "rank": 1, "predictedScore": 8.6, "confidence": 0.72, "reason": "Close to your top scores." },
              { "id": 9, "rank": 2, "predictedScore": 7.1, "confidence": 0.5 }
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
    public void Orders_results_by_rank_rather_than_by_position()
    {
        // A model may emit them in any order. The rank is what states the order, so
        // the array's own sequence is not evidence of anything.
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 9, "rank": 2, "predictedScore": 7.1, "confidence": 0.5 },
              { "id": 7, "rank": 1, "predictedScore": 8.6, "confidence": 0.7 }
            ]
            """));

        Assert.False(result.HasErrors);
        Assert.Equal([7, 9], result.Response!.Results.Select(r => r.Id));
    }

    [Fact]
    public void An_envelope_is_optional()
    {
        // Models reliably return the array and unreliably return the wrapper, so its
        // absence cannot be what rejects an otherwise correct ranking.
        var result = Parser.Parse(
            """
            { "results": [ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 } ] }
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
              "results": [ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 } ]
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
              "results": [ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 } ]
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
    public void Prose_around_the_json_is_an_error_rather_than_something_to_salvage()
    {
        // The single most common failure from a small model, and D31 is explicit that
        // it is reported rather than repaired: stripping a fence here is the first
        // step towards inferring what the model meant.
        var result = Parser.Parse(
            """
            Sure! Here is your ranking:
            ```json
            { "results": [] }
            ```
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("not valid JSON"));
    }

    [Fact]
    public void A_bare_array_is_an_error_that_says_what_was_expected()
    {
        var result = Parser.Parse("""[ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 } ]""");

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
    [InlineData("rank")]
    [InlineData("predictedScore")]
    [InlineData("confidence")]
    public void Every_required_field_is_required(string omitted)
    {
        var fields = new Dictionary<string, string>
        {
            ["id"] = "7",
            ["rank"] = "1",
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
            """[ { "id": "7", "rank": 1, "predictedScore": 8.0, "confidence": 0.6 } ]"""));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("whole number"));
    }

    [Fact]
    public void A_title_ranked_twice_is_an_error()
    {
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 },
              { "id": 7, "rank": 2, "predictedScore": 7.0, "confidence": 0.6 }
            ]
            """));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("more than once"));
    }

    [Fact]
    public void A_rank_used_twice_is_an_error()
    {
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 },
              { "id": 9, "rank": 1, "predictedScore": 7.0, "confidence": 0.6 }
            ]
            """));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("rank 1"));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(10.5)]
    [InlineData(-1)]
    public void A_predicted_score_off_the_scale_is_an_error(double score)
    {
        var result = Parser.Parse(Wrap(
            $$"""[ { "id": 7, "rank": 1, "predictedScore": {{score}}, "confidence": 0.6 } ]"""));

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
            $$"""[ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": {{confidence}} } ]"""));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Message.Contains("outside 0–1"));
    }

    [Fact]
    public void A_rank_below_one_is_an_error()
    {
        var result = Parser.Parse(Wrap(
            """[ { "id": 7, "rank": 0, "predictedScore": 8.0, "confidence": 0.6 } ]"""));

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void A_gap_in_the_ranks_is_a_warning_and_still_applies()
    {
        var result = Parser.Parse(Wrap(
            """
            [
              { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 },
              { "id": 9, "rank": 5, "predictedScore": 7.0, "confidence": 0.6 }
            ]
            """));

        Assert.False(result.HasErrors);
        Assert.Contains(result.Problems, p => p.Severity == ScoringSeverity.Warning);
        Assert.Equal(2, result.Response!.Results.Count);
    }

    [Fact]
    public void An_overlong_reason_is_shortened_and_warned_about_rather_than_refused()
    {
        var reason = new string('a', 900);

        var result = Parser.Parse(Wrap(
            $$"""[ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6, "reason": "{{reason}}" } ]"""));

        Assert.False(result.HasErrors);
        Assert.Equal(500, result.Response!.Results[0].Reason!.Length);
        Assert.Contains(result.Problems, p => p.Severity == ScoringSeverity.Warning);
    }

    [Fact]
    public void A_response_larger_than_the_limit_is_refused_before_it_is_parsed()
    {
        var parser = new ScoringResponseParser(new ScoringLimits { MaxBytes = 64 });

        var result = parser.Parse(Wrap(
            """[ { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 } ]"""));

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
              { "id": 7, "rank": 1, "predictedScore": 8.0, "confidence": 0.6 },
              { "id": 9, "rank": 2, "predictedScore": 99, "confidence": 0.6 },
              { "id": 11, "rank": 3, "predictedScore": 6.0, "confidence": 0.6 }
            ]
            """));

        Assert.True(result.HasErrors);
        Assert.Equal([7, 11], result.Response!.Results.Select(r => r.Id));
    }
}

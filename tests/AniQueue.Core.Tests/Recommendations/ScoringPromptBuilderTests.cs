using AniQueue.Core.Recommendations;

namespace AniQueue.Core.Tests.Recommendations;

/// <summary>
/// The prompt is half of a contract whose other half is
/// <see cref="ScoringResponseParser"/>, so the test that matters is that the shape
/// it asks for is one that parser accepts. The rest is wording, which these assert
/// only where the wording is load-bearing.
/// </summary>
public class ScoringPromptBuilderTests
{
    private static ScoringRequest Request(int candidates = 2, int available = 2, int history = 2) => new()
    {
        GeneratedAt = DateTimeOffset.UnixEpoch,
        HistoryAvailable = available,
        History = Enumerable.Range(1, history)
            .Select(i => new ScoringHistoryEntry { Title = $"Rated {i}", Score = 7 })
            .ToList(),
        Candidates = Enumerable.Range(1, candidates)
            .Select(i => new ScoringCandidate { Id = i, Title = $"Waiting {i}" })
            .ToList()
    };

    [Fact]
    public void The_example_it_asks_for_is_one_the_parser_accepts()
    {
        // The whole point of the file. If the prompt drifts from the parser, every
        // reply is rejected and the failure looks like a bad model rather than a bad
        // instruction.
        var result = new ScoringResponseParser().Parse(ScoringPromptBuilder.Schema(ScoringScale.Default));

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Response!.Results.Count);
    }

    [Fact]
    public void Says_how_many_titles_are_waiting()
    {
        Assert.Contains("7 titles they have not watched", ScoringPromptBuilder.Build(Request(candidates: 7)));
    }

    [Fact]
    public void Says_when_the_history_is_a_sample()
    {
        var prompt = ScoringPromptBuilder.Build(Request(available: 566, history: 200));

        Assert.Contains("sample of their 566 rated titles", prompt);
    }

    [Fact]
    public void Does_not_call_a_complete_history_a_sample()
    {
        Assert.DoesNotContain("sample", ScoringPromptBuilder.Build(Request(available: 2, history: 2)));
    }

    [Fact]
    public void Forbids_the_two_things_that_actually_break_a_reply()
    {
        // Prose around the object and an invented id are the failures that reach the
        // parser, so they are stated rather than implied.
        var prompt = ScoringPromptBuilder.Build(Request());

        Assert.Contains("JSON only", prompt);
        Assert.Contains("no code fence", prompt);
        Assert.Contains("Never invent one", prompt);
    }

    /// <summary>
    /// The reply may not claim the person rated a candidate.
    /// </summary>
    /// <remarks>
    /// Every candidate is unwatched by definition, so a rating of one cannot exist.
    /// Two different models invented one anyway: qwen3-vl-8b answered "You rated
    /// 'Haite Kudasai, Takamine-san' 6.0." about an unwatched title, and gpt-oss-20b
    /// answered "Sci-fi thriller like Psycho-Pass you rated 7." about Psycho-Pass
    /// itself. In both the number was the model's own predictedScore, which is what
    /// makes the ranking untrustworthy rather than merely the sentence wrong.
    /// </remarks>
    [Fact]
    public void Says_that_candidates_are_unwatched_and_cannot_have_been_rated()
    {
        var prompt = ScoringPromptBuilder.Build(Request());

        Assert.Contains("never say they rated one", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forbids_quoting_its_own_score_back_as_a_rating()
    {
        var prompt = ScoringPromptBuilder.Build(Request());

        Assert.Contains("Never put your own predictedScore in the reason", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is something to say when the history has no answer.
    /// </summary>
    /// <remarks>
    /// The instruction this replaced — "referring to their history where you can" —
    /// gave a model no way to decline, so one that found nothing close invented
    /// something rather than saying so. An escape hatch is what stops a request for
    /// grounding from becoming a request for fiction.
    /// </remarks>
    [Fact]
    public void Offers_a_way_to_say_there_is_no_comparison()
    {
        var prompt = ScoringPromptBuilder.Build(Request());

        Assert.Contains("If nothing in their history is close", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worked example does not demonstrate the thing the rules forbid.
    /// </summary>
    /// <remarks>
    /// It used to: a reason of "Same director as one you rated 9." sat beside a
    /// predictedScore of 9.5 — a rating in the reason, adjacent to the score, attached
    /// to no title anybody could check. A model reproduces the example far more
    /// faithfully than it obeys the prose, so the example was teaching the failure the
    /// prose now forbids.
    /// </remarks>
    [Fact]
    public void The_example_reasons_quote_no_rating()
    {
        var example = ScoringPromptBuilder.Schema(ScoringScale.Default);

        // Every number in the example belongs to a field. None belongs to a sentence
        // about what the person scored something.
        Assert.DoesNotContain("you rated", example, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_example_shows_what_to_say_when_nothing_matches()
    {
        var example = ScoringPromptBuilder.Schema(ScoringScale.Default);

        Assert.Contains("Nothing close in your history", example, StringComparison.Ordinal);
    }

    [Fact]
    public void Asks_for_all_to_be_weighed_before_asking_for_only_some_back()
    {
        var prompt = ScoringPromptBuilder.Build(Request(candidates: 100) with { ReturnTop = 20 });

        // The order of these two clauses is the whole instruction. A model told only
        // "return 20" tends to rank the first twenty it reads, which is a different
        // and much worse answer than the best twenty — and one that looks identical
        // in the reply, so nothing downstream could catch it.
        Assert.Contains("Weigh ALL 100 candidates, then return only the best 20", prompt);
        Assert.Contains("Do not rank the first 20 you see", prompt);
        Assert.Contains("Return exactly 20 results, ranked 1 to 20", prompt);
    }

    [Fact]
    public void Does_not_mention_a_limit_that_narrows_nothing()
    {
        // Asking for the top 100 of 100 is an instruction that is really a no-op, and
        // a no-op instruction is one more thing for a small model to misread.
        var prompt = ScoringPromptBuilder.Build(Request(candidates: 100) with { ReturnTop = 100 });

        Assert.DoesNotContain("Weigh ALL", prompt);
        Assert.Contains("rank as many as you can and stop", prompt);
    }

    [Fact]
    public void Permits_a_short_ranking_rather_than_demanding_all_of_them()
    {
        // A model told to rank all 182 or fail will fail. Telling it to stop early is
        // what makes the missing-candidate warning the common case rather than a
        // rejected reply.
        Assert.Contains("rank as many as you can and stop", ScoringPromptBuilder.Build(Request()));
    }
}

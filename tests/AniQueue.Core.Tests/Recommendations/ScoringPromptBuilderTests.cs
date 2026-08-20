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

    [Fact]
    public void Permits_a_short_ranking_rather_than_demanding_all_of_them()
    {
        // A model told to rank all 182 or fail will fail. Telling it to stop early is
        // what makes the missing-candidate warning the common case rather than a
        // rejected reply.
        Assert.Contains("rank as many as you can and stop", ScoringPromptBuilder.Build(Request()));
    }
}

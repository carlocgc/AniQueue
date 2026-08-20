namespace AniQueue.Core.Recommendations;

/// <summary>One title's place in the ranking a model returned.</summary>
public sealed record ScoringResult
{
    /// <summary>The AniQueue anime id, echoed back from the candidate.</summary>
    public required int Id { get; init; }

    /// <summary>1-based placement.</summary>
    public required int Rank { get; init; }

    /// <summary>What the model expects the user to rate it, on the request's scale.</summary>
    public required double PredictedScore { get; init; }

    /// <summary>How sure the model is, 0–1.</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// The model's stated justification, shown verbatim beside the score.
    /// </summary>
    /// <remarks>
    /// Optional, because a reason is an explanation rather than a rank and a model
    /// that omits it has still answered the question. Truncated rather than
    /// rejected when it runs long: an over-talkative model is a formatting problem,
    /// not a contract violation, and refusing an otherwise valid ranking over it
    /// would be the pipeline being strict about the wrong thing.
    /// </remarks>
    public string? Reason { get; init; }
}

/// <summary>
/// A ranking as it came back, after it has been read but before it has been
/// matched against a library.
/// </summary>
/// <remarks>
/// The structural half of validation lives here and needs no database: whether it
/// is JSON at all, whether the envelope says what it must, whether every result
/// carries the four fields, whether ids or ranks repeat, and whether the numbers
/// are in range. What is left — whether these ids name titles this profile is
/// actually planning to watch — is a question about the library, and is answered
/// by <c>IRecommendationService</c>.
/// </remarks>
public sealed record ScoringResponse
{
    /// <summary>The format name a response must declare.</summary>
    public const string ResponseFormat = "aniqueue-scoring-response";

    public IReadOnlyList<ScoringResult> Results { get; init; } = [];
}

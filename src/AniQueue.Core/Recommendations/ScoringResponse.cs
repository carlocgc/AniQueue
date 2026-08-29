namespace AniQueue.Core.Recommendations;

/// <summary>One title's score, as a model returned it.</summary>
/// <remarks>
/// There is deliberately no placement here: a model asked for both a rank and a
/// score will sometimes derive the score from the rank, and the score is what
/// leaves. A <c>rank</c> that arrives anyway is read past rather than refused.
/// </remarks>
public sealed record ScoringResult
{
    /// <summary>The AniQueue anime id, echoed back from the candidate.</summary>
    public required int Id { get; init; }

    /// <summary>What the model expects the user to rate it, on the request's scale.</summary>
    public required double PredictedScore { get; init; }

    /// <summary>How sure the model is, 0–1.</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// The model's stated justification, shown verbatim beside the score.
    /// </summary>
    /// <remarks>
    /// Optional, because a reason is an explanation rather than a score and a model
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
/// carries the three fields, whether ids repeat, and whether the numbers are in
/// range. What is left — whether these ids name titles this profile is
/// actually planning to watch — is a question about the library, and is answered
/// by <c>IRecommendationService</c>.
/// </remarks>
public sealed record ScoringResponse
{
    /// <summary>The format name a response must declare.</summary>
    public const string ResponseFormat = "aniqueue-scoring-response";

    /// <summary>
    /// The library key the reply echoed, or null when it echoed none.
    /// </summary>
    /// <remarks>
    /// Read here and judged elsewhere, which is the same split the rest of this type
    /// keeps: whether a key is <i>present and well formed</i> is a fact about the
    /// document, and whether it names <i>this</i> library is a question only something
    /// holding the database can answer. <c>IRecommendationService</c> answers it.
    /// </remarks>
    public string? Library { get; init; }

    public IReadOnlyList<ScoringResult> Results { get; init; } = [];
}

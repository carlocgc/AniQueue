namespace AniQueue.Core.Domain;

/// <summary>
/// One candidate's placement within a <see cref="RecommendationRun"/>. Retaining
/// these is what makes "compare this ranking to the previous one" possible.
///
/// Like <see cref="QueueItem"/>, a row refers to either an anime or a franchise,
/// never both and never neither.
///
/// Everything here originates from an external model and is treated as untrusted
/// data: values are validated on import and never executed or interpreted.
/// </summary>
public class RecommendationRunItem
{
    public int Id { get; set; }

    public int RunId { get; set; }

    public RecommendationRun? Run { get; set; }

    public int? AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public int? FranchiseId { get; set; }

    public Franchise? Franchise { get; set; }

    /// <summary>1-based placement as returned by the model.</summary>
    public int Rank { get; set; }

    /// <summary>The model's predicted score on the profile's scoring scale.</summary>
    public double PredictedScore { get; set; }

    /// <summary>The model's stated confidence, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>
    /// The model's stated justification. Displayed verbatim to explain a ranking,
    /// so it is HTML-encoded at render time like any other untrusted string.
    /// </summary>
    public string? Reason { get; set; }
}

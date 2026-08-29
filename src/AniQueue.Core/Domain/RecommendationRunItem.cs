namespace AniQueue.Core.Domain;

/// <summary>
/// One candidate's score within a <see cref="RecommendationRun"/>. Retaining these
/// is what makes comparing a ranking against the previous one possible.
///
/// Everything here originates from an external model and is treated as untrusted
/// data: values are validated on import and never executed or interpreted.
/// </summary>
public class RecommendationRunItem
{
    public int Id { get; set; }

    public int RunId { get; set; }

    public RecommendationRun? Run { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

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

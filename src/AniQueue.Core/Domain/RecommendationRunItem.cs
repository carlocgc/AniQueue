namespace AniQueue.Core.Domain;

/// <summary>
/// One candidate's score within a <see cref="RecommendationRun"/>. Retaining
/// these is what makes "compare this ranking to the previous one" possible.
///
/// A scored candidate is always a single title (D16). It could not be a group, and
/// the reason was structural rather than a matter of taste: applying a run caches
/// its result on <see cref="LibraryEntry"/>, a group had no such row, so a group's
/// placement could be stored and then never applied to anything. D23 has since
/// removed groups altogether, which settles it from the other direction too.
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

    // Rank stood here, 1-based, as the model returned it. D43 removed it from the
    // interchange, so there is nothing left to store: a score derived from a
    // placement is not a prediction about this person's taste, and the placement is
    // what invited the derivation.

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

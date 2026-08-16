namespace AniQueue.Core.Domain;

/// <summary>
/// Metadata for one recommendation exercise: what was sent for ranking, by which
/// provider, and whether the user accepted the result.
///
/// The request payload itself is not stored. The brief asked both to avoid
/// duplicating request data and to support comparing one recommendation set
/// against a previous one, which metadata alone cannot do — so the per-candidate
/// results live in <see cref="RecommendationRunItem"/> and the request is
/// reconstructable from them (D4).
/// </summary>
public class RecommendationRun
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Which provider produced the ranking. The MVP only has the manual
    /// copy-and-paste JSON provider; no network AI provider is required.
    /// </summary>
    public required string ProviderName { get; set; }

    /// <summary>
    /// Free text supplied by the user, e.g. the model they pasted the prompt into.
    /// Never used to make decisions — it is a record, not a control.
    /// </summary>
    public string? ModelIdentifier { get; set; }

    /// <summary>How many scored titles informed the ranking.</summary>
    public int CompletedCount { get; set; }

    /// <summary>How many candidates were sent for ranking.</summary>
    public int CandidateCount { get; set; }

    /// <summary>How many rankings came back. Less than CandidateCount means the model omitted some.</summary>
    public int ResultCount { get; set; }

    /// <summary>
    /// Whether this run's scores were written onto library entries. Importing a
    /// ranking never reorders the manual queue, so "applied" affects display
    /// ordering and nothing else.
    /// </summary>
    public bool WasApplied { get; set; }

    public ICollection<RecommendationRunItem> Items { get; set; } = [];
}

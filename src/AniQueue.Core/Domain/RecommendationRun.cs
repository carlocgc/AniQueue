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
    /// Which provider produced the ranking: the manual copy-and-paste JSON provider
    /// of Phase 7, or the hosted-endpoint provider of Phase 8. Both carry the same
    /// payload and are validated against the same schema (D31), so this records who
    /// carried it and nothing about what was allowed through. No API key is required
    /// for either, and neither has to be configured at all.
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

    /// <summary>
    /// How long the model took to answer, when anything measured it.
    /// </summary>
    /// <remarks>
    /// Null for the manual path, where the wait happened in somebody else's chat
    /// window and AniQueue has no idea how long it was. Written by Phase 8's endpoint,
    /// which does.
    ///
    /// It exists so that the second run onwards can say "your last one took six
    /// minutes" while a person waits on a request with no progress to report — a
    /// ranking arrives all at once, so there is nothing to show but elapsed time, and
    /// elapsed time means nothing without something to compare it to. That is the
    /// difference between a page that looks hung and one that looks busy.
    ///
    /// Stored as milliseconds because SQLite has no interval type and EF's TimeSpan
    /// mapping is a formatted string that cannot be compared or averaged in a query.
    /// </remarks>
    public long? DurationMilliseconds { get; set; }

    public ICollection<RecommendationRunItem> Items { get; set; } = [];
}

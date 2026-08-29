namespace AniQueue.Core.Domain;

/// <summary>
/// Metadata for one recommendation exercise: what was sent for ranking, by which
/// provider, and whether the user accepted the result. The request payload is not
/// stored — it is reconstructable from <see cref="RecommendationRunItem"/>.
/// </summary>
public class RecommendationRun
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Which provider carried the ranking: the manual copy-and-paste route or a
    /// hosted endpoint. Both send the same payload and are validated against the
    /// same schema, so this records who carried it and nothing more.
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
    /// Whether this run's scores were written onto library entries. Applying a
    /// ranking affects display ordering and never the manual queue.
    /// </summary>
    public bool WasApplied { get; set; }

    /// <summary>
    /// How long the model took to answer, in milliseconds. Null for the manual
    /// route, where the wait happened in somebody else's chat window. It lets a
    /// later run say how long the last one took while a request with no progress to
    /// report is in flight.
    /// </summary>
    /// <remarks>
    /// Milliseconds because SQLite has no interval type and EF's TimeSpan mapping
    /// is a formatted string that cannot be compared or averaged in a query.
    /// </remarks>
    public long? DurationMilliseconds { get; set; }

    public ICollection<RecommendationRunItem> Items { get; set; } = [];
}

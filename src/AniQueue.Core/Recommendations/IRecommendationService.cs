using AniQueue.Core.Domain;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Recommendations;

/// <summary>How much of the library goes into a request.</summary>
public sealed record ScoringRequestOptions
{
    public static ScoringRequestOptions Default { get; } = new();

    /// <summary>
    /// The most scored titles to send as history.
    /// </summary>
    /// <remarks>
    /// Two hundred, and the cap exists for the model this workflow targets. A
    /// measured library holds 566 scored titles, which is roughly 23 KB of history
    /// against 8 KB at this cap — affordable for a large hosted model and enough to
    /// crowd out the candidates on a small self-hosted one, where the failure mode
    /// is not an error but a worse ranking nobody can attribute to context.
    ///
    /// Most recent first, because a rating from twelve years ago describes a person
    /// who no longer exists as reliably as it describes this one. What is dropped is
    /// stated in the payload rather than silently omitted, so the sample is visible
    /// as a sample.
    /// </remarks>
    public int MaxHistory { get; init; } = 200;
}

/// <summary>One ranked title, resolved against the library it claims to describe.</summary>
public sealed record ScoringPreviewItem
{
    public required ScoringResult Result { get; init; }

    public required string Title { get; init; }

    /// <summary>Where the title currently sits, which is how a stale result is spotted.</summary>
    public required LibraryStatus Status { get; init; }

    /// <summary>The score this would replace, when the title already carries one.</summary>
    public double? PreviousScore { get; init; }

    /// <summary>
    /// Whether applying would write this row. False for a title that has left the
    /// backlog since the request was generated.
    /// </summary>
    public bool WillApply => Status == LibraryStatus.Planning;
}

/// <summary>
/// A ranking read, checked against the library, and not yet written anywhere.
/// </summary>
/// <remarks>
/// The same shape the import pipeline uses and for the same reason (D9): nothing
/// touches the database until the user has seen what would happen. What differs is
/// what a problem costs — an import loses one malformed record out of three
/// thousand, while a ranking with a rank collision is not a partial order but a
/// wrong one, so <see cref="ScoringSeverity.Error"/> stops the whole thing.
/// </remarks>
public sealed record ScoringPreview
{
    public IReadOnlyList<ScoringPreviewItem> Items { get; init; } = [];

    public IReadOnlyList<ScoringProblem> Problems { get; init; } = [];

    /// <summary>How many titles were waiting to be ranked when this was checked.</summary>
    public int CandidateCount { get; init; }

    public bool HasErrors => Problems.Any(p => p.Severity == ScoringSeverity.Error);

    /// <summary>How many rows applying would actually write.</summary>
    public int ApplicableCount => Items.Count(i => i.WillApply);

    /// <summary>Ranked titles that have since left the backlog, and so are skipped.</summary>
    public int StaleCount => Items.Count(i => !i.WillApply);

    /// <summary>Candidates sent that the ranking did not mention.</summary>
    public int MissingCount => Math.Max(0, CandidateCount - Items.Count(i => i.WillApply));

    public bool CanApply => !HasErrors && ApplicableCount > 0;
}

/// <summary>What applying a ranking did.</summary>
public sealed record ScoringApplyResult(int RunId, int Applied, int Skipped);

/// <summary>One past ranking, for the list of them.</summary>
public sealed record RecommendationRunSummary
{
    public required int Id { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string ProviderName { get; init; }

    public string? ModelIdentifier { get; init; }

    public int CandidateCount { get; init; }

    public int ResultCount { get; init; }

    public int CompletedCount { get; init; }

    public bool WasApplied { get; init; }
}

/// <summary>
/// Builds what a model is asked, and applies what it answers.
///
/// The two halves of Phase 7's contract meet here, and nothing between them knows
/// how the payload travelled: the manual copy-and-paste path and Phase 8's
/// configured endpoint both produce the same string and hand it to the same
/// <see cref="PreviewAsync"/> (D31). That is what makes the second additive rather
/// than a second pipeline.
/// </summary>
public interface IRecommendationService
{
    /// <summary>
    /// Assembles everything a model needs to rank this profile's backlog.
    /// </summary>
    /// <remarks>
    /// Candidates are the visible Planning entries — hidden ones are excluded,
    /// because the user has already said they do not want to see them and a ranking
    /// is a reason to see something.
    ///
    /// Personal notes travel only when
    /// <see cref="ProfileSettings.IncludePersonalNotesInAiExport"/> is set (§6).
    /// </remarks>
    Task<ScoringRequest> BuildRequestAsync(
        int profileId,
        ScoringRequestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a response and checks it against the library, writing nothing.
    /// </summary>
    /// <remarks>
    /// Takes the raw text rather than a parsed object so that every caller gets the
    /// same validation: a preview built from an already-trusted object would be a
    /// second way in, and the second way in is the one that skips a check.
    /// </remarks>
    Task<ScoringPreview> PreviewAsync(
        int profileId,
        string json,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a ranking: a <see cref="RecommendationRun"/> with its items, and the
    /// current result denormalised onto each entry (D4).
    /// </summary>
    /// <remarks>
    /// It touches <see cref="LibraryEntry.RecommendationScore"/>,
    /// <see cref="LibraryEntry.RecommendationConfidence"/>,
    /// <see cref="LibraryEntry.RecommendationReason"/> and
    /// <see cref="LibraryEntry.RecommendationUpdatedAt"/>. Nothing else — not
    /// status, not progress, not the user's own score, and above all not
    /// <see cref="QueueItem.Position"/>. The model proposes an order and the user
    /// owns one; D11 is why they are separate columns rather than one contested
    /// column, and this method is where that separation is either kept or lost.
    ///
    /// Refuses a preview whose problems include an error. Nothing is applied in
    /// part (D31).
    /// </remarks>
    Task<ScoringApplyResult> ApplyAsync(
        int profileId,
        ScoringPreview preview,
        string providerName,
        string? modelIdentifier = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Past runs, newest first.</summary>
    Task<IReadOnlyList<RecommendationRunSummary>> GetRunsAsync(
        int profileId,
        int take = 20,
        CancellationToken cancellationToken = default);
}

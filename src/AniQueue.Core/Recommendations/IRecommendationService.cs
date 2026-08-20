using AniQueue.Core.Domain;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Recommendations;

/// <summary>How much of the library goes into a request.</summary>
/// <remarks>
/// Both bounds are the user's, held on <see cref="ProfileSettings"/> and passed in
/// here so the service stays a function of what it is given. The right values are
/// properties of somebody else's model — its context window, and how well it holds
/// a long list together — which AniQueue has no way to see and should not guess at
/// past a first sensible answer.
/// </remarks>
public sealed record ScoringRequestOptions
{
    public static ScoringRequestOptions Default { get; } = new();

    /// <summary>The most scored titles to send as history. Zero sends none.</summary>
    /// <remarks>
    /// Most recent first, because a rating from twelve years ago describes a person
    /// who no longer exists as reliably as it describes this one. What is dropped is
    /// stated in the payload rather than silently omitted, so a sample is visible as
    /// a sample.
    /// </remarks>
    public int MaxHistory { get; init; } = 200;

    /// <summary>The most titles to offer for ranking, or null for all of them.</summary>
    /// <remarks>
    /// <b>Which ones a capped request takes is the whole design of this option.</b>
    /// Taking the first fifty alphabetically would mean the second half of the
    /// library is never ranked however many times it is run, which turns a cap into
    /// a permanent blind spot rather than a smaller batch.
    ///
    /// So they are taken least-recently-scored first, with never-scored titles ahead
    /// of everything: run a capped request repeatedly and it sweeps the backlog,
    /// covering what has never been looked at and then refreshing whatever is
    /// stalest. The cap becomes a page size rather than a horizon.
    /// </remarks>
    public int? MaxCandidates { get; init; }

    /// <summary>The bounds a stored preference is clamped into before it is used.</summary>
    /// <remarks>
    /// Applied where the settings are read rather than where they are written, so a
    /// row edited by hand or left behind by an older build cannot produce a request
    /// nothing can send. The ceilings are deliberately far above any sensible answer:
    /// they exist to stop a typed extra zero costing a minute of database work, not
    /// to second-guess someone who really does want everything.
    /// </remarks>
    public static ScoringRequestOptions From(int historySize, int? candidateLimit) => new()
    {
        MaxHistory = Math.Clamp(historySize, 0, 5_000),
        MaxCandidates = candidateLimit is { } limit ? Math.Clamp(limit, 1, 5_000) : null
    };
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
    /// Why this row is passed over, or null when it will be written.
    /// </summary>
    /// <remarks>
    /// A reason rather than a flag, because there is more than one way to be
    /// skipped and the row is where the answer is wanted: a title that has left the
    /// backlog and a title that was never offered are both passed over, and telling
    /// somebody which is which is the difference between a table they can act on and
    /// a column of greyed-out rows.
    /// </remarks>
    public string? SkippedBecause { get; init; }

    /// <summary>Whether applying would write this row.</summary>
    public bool WillApply => SkippedBecause is null;
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

    /// <summary>
    /// How many titles were offered for ranking.
    /// </summary>
    /// <remarks>
    /// What was <i>offered</i>, not what is waiting. Those differ the moment a
    /// candidate limit is set, and reporting the backlog here would tell a user who
    /// deliberately asked for fifty titles that a hundred and thirty-two are
    /// missing — turning their own setting into a warning against itself.
    /// </remarks>
    public int CandidateCount { get; init; }

    public bool HasErrors => Problems.Any(p => p.Severity == ScoringSeverity.Error);

    /// <summary>How many rows applying would actually write.</summary>
    public int ApplicableCount => Items.Count(i => i.WillApply);

    /// <summary>Ranked titles that are passed over, for any reason.</summary>
    public int SkippedCount => Items.Count(i => !i.WillApply);

    /// <summary>Offered titles the ranking did not mention.</summary>
    public int MissingCount => Math.Max(0, CandidateCount - ApplicableCount);

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
    /// <param name="offeredAnimeIds">
    /// What the request actually asked about, when the caller still holds it.
    /// </param>
    /// <remarks>
    /// Passed in rather than remembered, because the request is deliberately not
    /// stored — it is derivable from the run that results (D4) and storing it would
    /// mean a second copy of the backlog per attempt, most of them abandoned.
    ///
    /// It is what lets a ranking be checked against the question rather than against
    /// the library: with a candidate limit set those are different sets, and a title
    /// that is waiting but was never offered has not gone missing. When it is null
    /// the whole visible backlog is assumed, which is what it was.
    /// </remarks>
    Task<ScoringPreview> PreviewAsync(
        int profileId,
        string json,
        IReadOnlyCollection<int>? offeredAnimeIds = null,
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

    /// <summary>What the profile currently asks for, clamped into usable bounds.</summary>
    Task<ScoringRequestOptions> GetOptionsAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores how much of the library future requests carry.
    /// </summary>
    /// <remarks>
    /// A preference, so it lives in the database rather than in operator
    /// configuration (D20) — it describes a model the user chose, and they change it
    /// from the page where they will see the effect. Phase 10 offers the same two
    /// values beside the rest of the preferences; this is where they start.
    /// </remarks>
    Task SaveOptionsAsync(
        int profileId,
        ScoringRequestOptions options,
        CancellationToken cancellationToken = default);
}

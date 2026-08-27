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

    /// <summary>The most scored titles to send as history, or null for all of them.</summary>
    /// <remarks>
    /// Most recent first, because a rating from twelve years ago describes a person
    /// who no longer exists as reliably as it describes this one. What is dropped is
    /// stated in the payload rather than silently omitted, so a sample is visible as
    /// a sample.
    ///
    /// <b>Null means all of them, and zero is no longer a value this accepts.</b> It
    /// used to, and it meant "send none" — a ranking with no evidence of anyone's
    /// taste, which is a general opinion about anime rather than the thing this feature
    /// exists to produce. Nobody wants it, an empty field is what a person types when
    /// they mean "no limit", and the two sibling options beside it already read an
    /// empty field that way. One spelling for "everything" across the three.
    /// </remarks>
    public int? MaxHistory { get; init; } = 200;

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

    /// <summary>
    /// How many rankings to ask the model to return, or null for all of them.
    /// </summary>
    /// <remarks>
    /// Bounds the reply rather than the request, and the two are worth keeping apart
    /// because their costs are not alike: a long request is read once, while a long
    /// reply is generated a token at a time and can exhaust a model's output budget
    /// halfway down the list.
    ///
    /// It does not narrow what the model considers. Every candidate sent is still
    /// weighed against the history; this asks only for the best of them to come
    /// back, which is a different — and usually better — answer than sending fewer
    /// titles in the first place.
    /// </remarks>
    public int? ReturnTop { get; init; }

    /// <summary>
    /// Whether the user's own notes travel with the candidates (§6, opt in).
    /// </summary>
    /// <remarks>
    /// Carried here rather than read from the database beside the candidates, which
    /// is where it used to live. D36 moved it to <c>userconfig.json</c> with the
    /// rest of the scoring settings — it describes what leaves the machine, which
    /// is a property of the integration — and this is the only route by which the
    /// service learns it. A caller that says nothing gets false, which is the answer
    /// §6 requires when nobody has opted in.
    /// </remarks>
    public bool IncludePersonalNotes { get; init; }

    /// <summary>The bounds a stored preference is clamped into before it is used.</summary>
    /// <remarks>
    /// Applied where the settings are read rather than where they are written, so a
    /// file edited by hand or left behind by an older build cannot produce a request
    /// nothing can send. The ceilings are deliberately far above any sensible answer:
    /// they exist to stop a typed extra zero costing a minute of database work, not
    /// to second-guess someone who really does want everything.
    /// </remarks>
    public static ScoringRequestOptions From(
        int? historySize,
        int? candidateLimit,
        int? returnTop = null,
        bool includePersonalNotes = false) => new()
    {
        MaxHistory = historySize is { } history ? Math.Clamp(history, 1, 5_000) : null,
        MaxCandidates = candidateLimit is { } limit ? Math.Clamp(limit, 1, 5_000) : null,
        ReturnTop = returnTop is { } top ? Math.Clamp(top, 1, 5_000) : null,
        IncludePersonalNotes = includePersonalNotes
    };
}

/// <summary>
/// How large a request would be, without building the one that would be sent (D53).
/// </summary>
/// <remarks>
/// Two numbers rather than one, because the size of a request is a straight line and
/// the page needs both ends of it: a fixed cost that is almost entirely the history,
/// and a slope that is what one more title adds. A user moving the candidate limit
/// gets an answer without another database read.
///
/// <b>Measured rather than estimated, and that is the reason this exists at all.</b> A
/// candidate carrying three title variants and two external identifiers is several
/// times the size of one added by hand, and which of those a library has is not
/// something to guess at.
/// </remarks>
public sealed record ScoringSizeEstimate
{
    /// <summary>Titles waiting to be watched and visible.</summary>
    public required int CandidatesAvailable { get; init; }

    /// <summary>Rated titles this profile has.</summary>
    public required int HistoryAvailable { get; init; }

    /// <summary>Characters a one-title request costs, instructions included.</summary>
    public required int BaselineCharacters { get; init; }

    /// <summary>Characters each title after the first adds.</summary>
    public required int PerCandidateCharacters { get; init; }

    /// <summary>What a request covering <paramref name="candidates"/> titles would cost.</summary>
    public int CharactersFor(int candidates) =>
        BaselineCharacters + (Math.Max(candidates - 1, 0) * PerCandidateCharacters);
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

    /// <summary>
    /// How many rankings the request asked to come back.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="CandidateCount"/> as soon as a return limit is set:
    /// asking for the best fifty of a hundred and eighty-two makes a reply of fifty
    /// a complete answer, and measuring it against the hundred and eighty-two would
    /// report a hundred and thirty-two omissions the user deliberately asked for.
    /// </remarks>
    public int ExpectedCount { get; init; }

    public bool HasErrors => Problems.Any(p => p.Severity == ScoringSeverity.Error);

    /// <summary>How many rows applying would actually write.</summary>
    public int ApplicableCount => Items.Count(i => i.WillApply);

    /// <summary>Ranked titles that are passed over, for any reason.</summary>
    public int SkippedCount => Items.Count(i => !i.WillApply);

    /// <summary>Rankings that were asked for and did not arrive.</summary>
    public int MissingCount => Math.Max(0, ExpectedCount - ApplicableCount);

    public bool CanApply => !HasErrors && ApplicableCount > 0;
}

/// <summary>What applying a ranking did.</summary>
public sealed record ScoringApplyResult(int RunId, int Applied, int Skipped);

/// <summary>
/// How much of the backlog carries a ranking worth trusting.
/// </summary>
/// <remarks>
/// The read half of D39. A score is not wrong because time passed — it is wrong
/// because the history it was predicted against has grown, so "stale" means rated
/// titles have been added since this one was scored rather than that a clock ran out.
///
/// Phase 8d's sweep picks its batches from exactly these two groups, never-scored
/// first and then stalest. Both halves therefore come from one query, so what the
/// page reports and what the job does cannot describe different backlogs.
/// </remarks>
public sealed record ScoringCoverage
{
    /// <summary>Titles waiting to be watched and visible.</summary>
    public required int Waiting { get; init; }

    /// <summary>Of those, how many carry a score at all.</summary>
    public required int Ranked { get; init; }

    /// <summary>Of those ranked, how many were scored before the taste they predict.</summary>
    public required int Stale { get; init; }

    public int Unranked => Math.Max(Waiting - Ranked, 0);

    public int UpToDate => Math.Max(Ranked - Stale, 0);

    /// <summary>
    /// How many of the three groups actually have anything in them.
    /// </summary>
    /// <remarks>
    /// Whether a breakdown is worth showing as a breakdown. With one group the total
    /// equals it, so printing both says one number twice and invites the reader to
    /// look for a difference that cannot exist.
    /// </remarks>
    public int Parts =>
        (UpToDate > 0 ? 1 : 0) + (Stale > 0 ? 1 : 0) + (Unranked > 0 ? 1 : 0);

    /// <summary>Whether there is anything left for a ranking run to usefully do.</summary>
    public bool IsSettled => Waiting > 0 && Unranked == 0 && Stale == 0;

    /// <summary>Whether the backlog has never been ranked at all.</summary>
    public bool IsUntouched => Ranked == 0;
}

/// <summary>
/// Everything known about why one title carries the score it does.
/// </summary>
/// <remarks>
/// Read from the run that produced it rather than from the columns denormalised
/// onto the entry (D4). Those exist so the backlog can sort by score in one
/// query; they deliberately do not carry which run wrote them, and the run is
/// where "when" and "by what" live. Asking the run also means this cannot drift
/// from the history the user can browse.
/// </remarks>
public sealed record RecommendationDetail
{
    // Rank, and the CandidateCount it was shown against, both stood here. Together
    // they rendered "Ranked 3 of 50" on a title — a number meaningful only inside a
    // batch the user never sees, and since D43 not a number at all. How many
    // candidates a run weighed is still carried by RecommendationRunSummary, where
    // it is a fact about the run rather than a claim about one title.

    public required double PredictedScore { get; init; }

    public required double Confidence { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset DeterminedAt { get; init; }

    /// <summary>How the ranking was carried — the manual path, or an endpoint.</summary>
    public required string ProviderName { get; init; }

    /// <summary>What the user said produced it, when they said anything.</summary>
    public string? ModelIdentifier { get; init; }
}

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

    /// <summary>How long the model took, when anything measured it.</summary>
    /// <remarks>
    /// Read back so the page can quote the last endpoint run as a scale for the next
    /// one. Null for a manual ranking, and for anything applied before this was
    /// recorded.
    /// </remarks>
    public TimeSpan? Duration { get; init; }
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
    /// Candidates are the Planning entries. Hidden ones used to be excluded on the
    /// grounds that the user had said they did not want to see them; Phase 18b
    /// deleted hiding, and a title nobody wants ranked comes off the source list it
    /// arrived on (D11).
    ///
    /// Personal notes travel only when
    /// <see cref="ProfileSettings.IncludePersonalNotesInAiExport"/> is set (§6).
    /// </remarks>
    /// <param name="history">
    /// A history read earlier and reused, or null to read it now. A sweep passes one
    /// so that every batch predicts against identical evidence; see
    /// <see cref="ScoringHistorySnapshot"/>.
    /// </param>
    Task<ScoringRequest> BuildRequestAsync(
        int profileId,
        ScoringRequestOptions? options = null,
        ScoringHistorySnapshot? history = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the history once, for a caller that will build several requests from it.
    /// </summary>
    Task<ScoringHistorySnapshot> BuildHistoryAsync(
        int profileId,
        int? maxHistory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Measures what a request would cost without building the one that would be sent
    /// (D53).
    /// </summary>
    /// <remarks>
    /// The page above the Remote card says how many tokens a run would be before anybody
    /// asks for one, and this is what answers it. It reads the backlog once and the
    /// history once, whatever the answer covers.
    ///
    /// <b>It used to be two calls to <see cref="BuildRequestAsync"/>, one for a request
    /// of a single candidate and one for two.</b> That read and serialised the whole
    /// history twice to render one number, on a page whose initialiser runs twice per
    /// visit under prerendering — so four full history reads to show a figure nobody had
    /// asked for yet. It also logged both of them at the level a real request logs at,
    /// which is why they looked alarming in a server log.
    ///
    /// <paramref name="options"/> is read for its history size and its notes flag, which
    /// are what change the size of a request. Its candidate limit is ignored: the
    /// measurement always probes with two, and the caller multiplies.
    /// </remarks>
    Task<ScoringSizeEstimate> MeasureAsync(
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
    /// <param name="request">
    /// The question this is an answer to, when the caller still holds it.
    /// </param>
    /// <remarks>
    /// Passed in rather than remembered, because the request is deliberately not
    /// stored — it is derivable from the run that results (D4), and keeping one per
    /// attempt would mean a second copy of the backlog for every reply, most of them
    /// abandoned.
    ///
    /// The whole request rather than the ids alone, because two different things are
    /// checked against it: which titles were offered, and how many rankings were
    /// asked for. Both differ from "the backlog" the moment either limit is set, and
    /// a reply is only short or complete relative to what was requested. When it is
    /// null the whole visible backlog is assumed, ranked in full.
    /// </remarks>
    /// <param name="route">
    /// How the reply arrived, which decides whether it must name the database it was
    /// built against (D50). No default: a caller that did not say would get the
    /// lenient answer to the one question that stops a wrong reply being applied.
    /// </param>
    Task<ScoringPreview> PreviewAsync(
        int profileId,
        ScoringRoute route,
        string json,
        ScoringRequest? request = null,
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
    /// <param name="duration">
    /// How long the model took, when anything measured it.
    /// </param>
    /// <remarks>
    /// Null for the manual path, where the wait happened in somebody else's chat
    /// window. Recorded so the next run can say "your last one took six minutes" while
    /// a person waits on one that reports no progress — a ranking arrives all at once,
    /// so elapsed time is the only honest thing to show and it means nothing without
    /// something to compare it to.
    /// </remarks>
    Task<ScoringApplyResult> ApplyAsync(
        int profileId,
        ScoringPreview preview,
        string providerName,
        string? modelIdentifier = null,
        IProgress<OperationProgress>? progress = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Why one title carries the score it does, from the most recent ranking that
    /// placed it. Null when no applied run mentions it.
    /// </summary>
    /// <remarks>
    /// Per title rather than per page, because it answers a question about one row
    /// that is only asked about a few of them: the reasoning is a paragraph, and
    /// joining it onto every listing would carry a page of prose to render a column
    /// of numbers.
    /// </remarks>
    Task<RecommendationDetail?> GetDetailAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default);

    /// <summary>Past runs, newest first.</summary>
    Task<IReadOnlyList<RecommendationRunSummary>> GetRunsAsync(
        int profileId,
        int take = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How much of the backlog is ranked, and how much of that is worth trusting.
    /// </summary>
    /// <param name="staleAfterRatings">
    /// How many further titles must be rated before a score counts as stale (D39).
    /// </param>
    /// <remarks>
    /// Counted rather than listed, because the question it answers — "is there
    /// anything left to do?" — needs three numbers and not several hundred rows.
    /// </remarks>
    // GetLastRunAtAsync was here, and answered what the sweep decided "due" against.
    // Phase 15b took that question away: due-ness is read from JobRun, which every
    // run writes. The reason it lost is worth keeping, because the argument for it
    // was right — a schedule must survive a restart, so it cannot be a field on the
    // job. What it got wrong was the table: this one records a run that *applied* a
    // ranking, so a sweep that ran and scored nothing, failed, or was cancelled read
    // as one that had never happened, and the next tick started it again.

    Task<ScoringCoverage> GetCoverageAsync(
        int profileId,
        int staleAfterRatings,
        CancellationToken cancellationToken = default);

    // Reading and writing the request sizes used to live here, on ProfileSettings.
    // D36 moved them to userconfig.json, and with them the last reason this service
    // owned a setting: it already takes ScoringRequestOptions as an argument, so a
    // caller now reads them from IUserSettingsStore and passes them in. The service
    // is a function of what it is given again, which is what it always claimed to be.
}

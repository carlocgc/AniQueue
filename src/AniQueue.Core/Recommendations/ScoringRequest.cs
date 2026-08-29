using AniQueue.Core.Domain;

namespace AniQueue.Core.Recommendations;

/// <summary>
/// The scale scores are expressed on, stated rather than assumed.
/// </summary>
/// <remarks>
/// A model has no way to know what "8" means unless it is told, and every source
/// AniQueue reads normalises into 1–10 on the way in (D-none; see the AniList
/// parser's scoring notes). Sending the bounds makes the response checkable: a
/// predicted score outside them is a validation error rather than a number the
/// application has to guess the meaning of.
/// </remarks>
public sealed record ScoringScale(int Min, int Max)
{
    /// <summary>What every parser normalises into, and so what every score means here.</summary>
    public static ScoringScale Default { get; } = new(1, 10);

    public bool Contains(double score) => score >= Min && score <= Max;
}

/// <summary>
/// One title the user has finished and scored — the evidence a ranking is personal
/// rather than general.
/// </summary>
/// <remarks>
/// Deliberately narrow. A model ranking a backlog needs to know what this person
/// liked, which is a title and a number; episode counts and identifiers would
/// treble the size of the largest part of the payload to say nothing about taste.
/// </remarks>
public sealed record ScoringHistoryEntry
{
    public required string Title { get; init; }

    /// <summary>The user's own rating on <see cref="ScoringScale"/>.</summary>
    public required int Score { get; init; }

    public MediaType MediaType { get; init; }

    public int? Year { get; init; }
}

/// <summary>Every identifier a candidate carries, keyed by the service that issued it.</summary>
public sealed record ScoringCandidateIds
{
    public string? AniList { get; init; }

    public string? MyAnimeList { get; init; }

    public bool Any => AniList is not null || MyAnimeList is not null;
}

/// <summary>The title variants a source published, where it published more than one.</summary>
public sealed record ScoringCandidateTitles
{
    public string? Romaji { get; init; }

    public string? English { get; init; }

    public string? Native { get; init; }

    public bool Any => Romaji is not null || English is not null || Native is not null;
}

/// <summary>
/// A history read once, so that several requests can be built from the same evidence.
/// </summary>
/// <remarks>
/// A sweep is many requests and should be one opinion. Reading the history per batch
/// would let a sync landing mid-sweep change the evidence underneath it, and the
/// scores from either side then land in one column and get sorted against each
/// other.
///
/// It also settles the server's prompt cache, from the other end to
/// <see cref="ScoringRequestWriter"/>: that class keeps the varying fields out of
/// the prefix, and this keeps the prefix's own contents from varying. The history is
/// around 95% of a batch's payload.
///
/// The cost is that a long sweep's last batch predicts against evidence as old as
/// the sweep, so a rating added at minute two does not influence the run it is added
/// during.
/// </remarks>
public sealed record ScoringHistorySnapshot
{
    /// <summary>The sample sent, already capped and ordered.</summary>
    public required IReadOnlyList<ScoringHistoryEntry> Entries { get; init; }

    /// <summary>How many rated titles existed when this was read.</summary>
    /// <remarks>
    /// Frozen with the entries rather than counted per batch, because the prompt states
    /// it — "a sample of their N rated titles" — and a figure that moved while the
    /// sample did not would describe a request that was never sent.
    /// </remarks>
    public required int Available { get; init; }
}

/// <summary>One title waiting to be watched, offered to be ranked.</summary>
public sealed record ScoringCandidate
{
    /// <summary>
    /// The AniQueue anime id, and the only thing a response is matched back on.
    /// </summary>
    /// <remarks>
    /// Not an external id, and not the title. External ids are absent for manual
    /// and MyAnimeList-only rows and a title is rewritten wholesale whenever the
    /// displayed language changes — either would make a response that was
    /// valid when generated stop matching the library it came from. The identifier
    /// that is stable for exactly as long as the row exists is the row's own.
    /// </remarks>
    public required int Id { get; init; }

    /// <summary>The title as AniQueue displays it, in the profile's language.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The other variants, so a model can recognise a show it knows under a
    /// different name. Omitted from the payload entirely when there are none.
    /// </summary>
    public ScoringCandidateTitles Titles { get; init; } = new();

    /// <summary>The format, which is a coarse statement about taste.</summary>
    /// <remarks>
    /// <b>Episode count and episode duration stood beside this and were removed.</b>
    /// Neither survived the question they were asked: does this change a predicted score?
    /// A model that recognises the title already knows how long it is, and one that does
    /// not is told by the prompt to answer with low confidence rather than infer — so
    /// neither case reached for them. <see cref="ScoringHistoryEntry"/> had already made
    /// the same finding from the other end, where episode counts "say nothing about
    /// taste".
    ///
    /// What is left is what history also carries, which is the point: the model compares
    /// like with like across the two halves of the payload rather than reading richer
    /// rows on one side.
    /// </remarks>
    public MediaType MediaType { get; init; }

    public int? Year { get; init; }

    /// <summary>
    /// What other services call this title, so a model can recognise it by an id it
    /// already knows rather than by a name it has to match.
    /// </summary>
    public ScoringCandidateIds ExternalIds { get; init; } = new();

    /// <summary>
    /// The user's own notes, present only when they have opted in
    /// (<see cref="Domain.ProfileSettings.IncludePersonalNotesInAiExport"/>).
    /// </summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Everything a model is given in order to rank a backlog: who is asking, what
/// they have liked, and what is waiting.
/// </summary>
/// <remarks>
/// One half of the scoring contract; the other is <see cref="ScoringResponse"/>.
/// Both are carried either by the user through copy and paste or by a configured
/// endpoint, and the payload does not know which.
///
/// Nothing here identifies a person: no account names, no email address, no
/// credentials, and no notes unless they were explicitly opted in.
/// </remarks>
public sealed record ScoringRequest
{
    /// <summary>The format name written into, and required back out of, the envelope.</summary>
    public const string RequestFormat = "aniqueue-scoring-request";

    /// <summary>The only version this build writes or accepts.</summary>
    public const int CurrentVersion = 1;

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Which library this request is about — <see cref="Domain.Profile.LibraryKey"/>,
    /// written into the envelope and asked for back.
    /// </summary>
    /// <remarks>
    /// Null only when a caller builds a request without one, which in practice means
    /// a test. A request with no key produces a reply with no key, and a reply with
    /// no key is checked exactly as replies were before this existed.
    /// </remarks>
    public string? Library { get; init; }

    public ScoringScale Scale { get; init; } = ScoringScale.Default;

    /// <summary>Scored, completed titles. Capped; see <see cref="HistoryAvailable"/>.</summary>
    public IReadOnlyList<ScoringHistoryEntry> History { get; init; } = [];

    /// <summary>
    /// How many scored titles the profile actually has, when more exist than were
    /// sent.
    /// </summary>
    /// <remarks>
    /// Sent so the number is visible rather than silent. A user comparing "566
    /// completed" on the dashboard against 200 lines of history should be able to
    /// see that the difference is a cap and not a bug, and a model reading it knows
    /// it is seeing a sample.
    /// </remarks>
    public int HistoryAvailable { get; init; }

    public IReadOnlyList<ScoringCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// How many titles are waiting to be watched, when the request carries only some
    /// of them.
    /// </summary>
    /// <remarks>
    /// Stated for the same reason <see cref="HistoryAvailable"/> is, and it earns its
    /// place twice over: the page needs it to say "50 of your 182, the ones longest
    /// without a score", and a model reading it knows it is ranking a slice rather
    /// than a whole backlog — which is the difference between "this is the worst of
    /// your titles" and "this is the worst of the ones I was shown".
    /// </remarks>
    public int CandidatesAvailable { get; init; }

    /// <summary>
    /// How many rankings this request asks for, or null for one per candidate.
    /// </summary>
    /// <remarks>
    /// Carried on the request rather than held only by whoever built it, because it
    /// is part of the question and the answer is checked against it: a reply with
    /// fifty rankings is complete when fifty were asked for and short when a hundred
    /// were.
    /// </remarks>
    public int? ReturnTop { get; init; }

    public bool IsHistoryCapped => HistoryAvailable > History.Count;

    public bool IsCandidatesCapped => CandidatesAvailable > Candidates.Count;

    /// <summary>How many results a complete reply to this request holds.</summary>
    public int ExpectedResults => ReturnTop is { } top ? Math.Min(top, Candidates.Count) : Candidates.Count;

    public bool IsRankingLimited => ExpectedResults < Candidates.Count;
}

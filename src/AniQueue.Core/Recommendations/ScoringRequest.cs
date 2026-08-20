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

/// <summary>One title waiting to be watched, offered to be ranked.</summary>
public sealed record ScoringCandidate
{
    /// <summary>
    /// The AniQueue anime id, and the only thing a response is matched back on.
    /// </summary>
    /// <remarks>
    /// Not an external id, and not the title. External ids are absent for manual
    /// and MyAnimeList-only rows and a title is rewritten wholesale whenever the
    /// displayed language changes (D22) — either would make a response that was
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

    public MediaType MediaType { get; init; }

    public int? Episodes { get; init; }

    public int? EpisodeMinutes { get; init; }

    public int? Year { get; init; }

    /// <summary>
    /// What other services call this title, so a model can recognise it by an id it
    /// already knows rather than by a name it has to match (D17).
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
/// This is one half of the contract Phase 7 exists to define; the other is
/// <see cref="ScoringResponse"/>. Both are carried either by the user, through
/// copy and paste, or by a configured endpoint in Phase 8 — the payload does not
/// know which, and D31 turns on it not needing to.
///
/// Nothing here identifies a person. §6's privacy rule is that an export carries
/// what ranking needs and nothing else: no account names, no email address, no
/// credentials, and no notes unless they were explicitly opted in.
/// </remarks>
public sealed record ScoringRequest
{
    /// <summary>The format name written into, and required back out of, the envelope.</summary>
    public const string RequestFormat = "aniqueue-scoring-request";

    /// <summary>The only version this build writes or accepts.</summary>
    public const int CurrentVersion = 1;

    public required DateTimeOffset GeneratedAt { get; init; }

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

    public bool IsHistoryCapped => HistoryAvailable > History.Count;

    public bool IsCandidatesCapped => CandidatesAvailable > Candidates.Count;
}

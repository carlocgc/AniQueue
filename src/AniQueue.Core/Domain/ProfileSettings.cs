namespace AniQueue.Core.Domain;

/// <summary>
/// Per-profile preferences. A typed entity rather than a key/value bag (D7): the
/// set of settings is fixed and known, so typed columns stay migratable and bind
/// directly to the Settings page instead of rotting into stringly-typed soup.
///
/// Recommendation *weighting* is deliberately absent. Weights configure a hybrid
/// ranking formula, and D32 withdrew the one that was planned along with the
/// surfaces that would have consumed it. Adding columns for a formula nothing
/// computes would be guessing at the shape of something nobody has asked for
/// twice.
/// </summary>
public class ProfileSettings
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    // General

    public required string DisplayName { get; set; }

    /// <summary>
    /// How many Up Next entries a preview of the queue shows.
    /// </summary>
    /// <remarks>
    /// Nothing reads this. It was written for the dashboard panel D32 declined, and
    /// is kept because Phase 10 offers it as a preference either way and Phase 11
    /// squashes the migration history — so a column with no consumer costs a line
    /// here and nothing at all in the shipped schema.
    /// </remarks>
    public int DefaultQueueSize { get; set; } = 10;

    /// <summary>A .NET date format string applied when rendering dates.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// Which title variant a sync writes to <see cref="Anime.Title"/> (D22).
    /// </summary>
    /// <remarks>
    /// A preference rather than whatever the source happened to send: AniList
    /// publishes three variants and a MyAnimeList export one, so a first sync would
    /// otherwise rewrite the displayed name of most of the library on a choice
    /// nobody made.
    ///
    /// Romaji by default because it is what a MyAnimeList library already holds, so
    /// the default changes nothing for the user who arrived that way. Changing it
    /// triggers a sync rather than swapping columns — the next fetch rewrites the
    /// title through the same path that set it, which is why there is no migration
    /// and no half-swapped state to guard.
    ///
    /// This is a *user* preference and so lives here, in the database, rather than
    /// alongside the account in configuration (D20).
    /// </remarks>
    public TitleLanguage PreferredTitleLanguage { get; set; } = TitleLanguage.Romaji;

    // Backlog

    // Recommendations

    /// <summary>
    /// Which ordering the backlog opens in.
    /// </summary>
    /// <remarks>
    /// Also unread, and for the same reason as <see cref="DefaultQueueSize"/>:
    /// <see cref="RecommendationMode.Hybrid"/> needs a formula D32 withdrew, so
    /// only <see cref="RecommendationMode.Manual"/> and
    /// <see cref="RecommendationMode.Ai"/> are expressible today and the backlog's
    /// own sort already covers both.
    /// </remarks>
    public RecommendationMode DefaultRecommendationMode { get; set; } = RecommendationMode.Manual;

    /// <summary>
    /// Opt-in, defaulting to false. Personal notes are free text and may contain
    /// anything, so they are excluded from AI export unless the user explicitly
    /// asks for them (ROADMAP.md §6, privacy).
    /// </summary>
    public bool IncludePersonalNotesInAiExport { get; set; }

    /// <summary>
    /// How many scored titles a scoring request carries as history.
    /// </summary>
    /// <remarks>
    /// A preference rather than a constant because the right value is a property of
    /// somebody else's model, which AniQueue cannot see. Two hundred is what fits a
    /// modest context alongside a real backlog; a large hosted model can take every
    /// title the library holds, and a small one may need far fewer. Zero sends none,
    /// which is a legitimate answer — the ranking is then general rather than
    /// personal, and the user has said so deliberately.
    /// </remarks>
    public int RecommendationHistorySize { get; set; } = 200;

    /// <summary>
    /// The most titles to offer for ranking at once, or null for all of them.
    /// </summary>
    /// <remarks>
    /// Null rather than zero for "no limit", because zero here would mean a request
    /// with nothing in it to rank and the two should not share a value.
    ///
    /// The default is no limit, which is the behaviour that existed before this was
    /// configurable. A cap is the user's statement about their model, not something
    /// to impose on a library it may suit perfectly well — but when it is set, it is
    /// what turns "my model cannot read 182 titles" from a dead end into several
    /// smaller runs.
    /// </remarks>
    public int? RecommendationCandidateLimit { get; set; }
}

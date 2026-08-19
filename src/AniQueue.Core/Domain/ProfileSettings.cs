namespace AniQueue.Core.Domain;

/// <summary>
/// Per-profile preferences. A typed entity rather than a key/value bag (D7): the
/// set of settings is fixed and known, so typed columns stay migratable and bind
/// directly to the Settings page instead of rotting into stringly-typed soup.
///
/// Recommendation *weighting* is deliberately absent. Weights configure the
/// hybrid ranking formula, and that formula is defined in Phase 9 — adding
/// columns for it now would be guessing at its shape.
/// </summary>
public class ProfileSettings
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    // General

    public required string DisplayName { get; set; }

    /// <summary>How many Up Next entries the dashboard shows.</summary>
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

    public RecommendationMode DefaultRecommendationMode { get; set; } = RecommendationMode.Manual;

    /// <summary>
    /// Opt-in, defaulting to false. Personal notes are free text and may contain
    /// anything, so they are excluded from AI export unless the user explicitly
    /// asks for them (ROADMAP.md §6, privacy).
    /// </summary>
    public bool IncludePersonalNotesInAiExport { get; set; }
}

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

    // Backlog

    /// <summary>
    /// Whether entries flagged <see cref="Anime.OptionalWithinFranchise"/> — specials,
    /// side OVAs — appear in backlog listings and count toward franchise completion.
    /// </summary>
    public bool ShowOptionalFranchiseEntries { get; set; }

    // Recommendations

    public RecommendationMode DefaultRecommendationMode { get; set; } = RecommendationMode.Manual;

    /// <summary>
    /// Opt-in, defaulting to false. Personal notes are free text and may contain
    /// anything, so they are excluded from AI export unless the user explicitly
    /// asks for them (ROADMAP.md §6, privacy).
    /// </summary>
    public bool IncludePersonalNotesInAiExport { get; set; }
}

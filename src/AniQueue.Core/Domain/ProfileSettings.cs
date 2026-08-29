namespace AniQueue.Core.Domain;

/// <summary>
/// Per-profile preferences: the things that describe how AniQueue looks to one
/// user. A typed entity rather than a key/value bag, because the set of settings
/// is fixed and known, so the columns stay migratable and bind directly to the
/// Settings page.
///
/// Integration settings — endpoints, limits, kill switches — live in
/// <c>userconfig.json</c> instead, because they still mean something while nobody
/// is looking at a page.
/// </summary>
public class ProfileSettings
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    // General

    public required string DisplayName { get; set; }

    /// <summary>How many Up Next entries a preview of the queue shows.</summary>
    public int DefaultQueueSize { get; set; } = 10;

    /// <summary>A .NET date format string applied when rendering dates.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// Which title variant a sync writes to <see cref="Anime.Title"/>. Romaji by
    /// default, which is what a MyAnimeList library already holds. Changing it
    /// triggers a sync rather than swapping columns, so the title is rewritten
    /// through the same path that set it.
    /// </summary>
    public TitleLanguage PreferredTitleLanguage { get; set; } = TitleLanguage.Romaji;

    /// <summary>Which ordering the backlog opens in.</summary>
    public RecommendationMode DefaultRecommendationMode { get; set; } = RecommendationMode.Manual;
}

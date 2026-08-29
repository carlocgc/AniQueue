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

    public required string DisplayName { get; set; }

    /// <summary>
    /// Which palette the application renders in.
    /// </summary>
    /// <remarks>
    /// Resolved during the server-side render and written as <c>data-theme</c> on
    /// <c>&lt;html&gt;</c>, so the first paint is already correct. Read after the
    /// circuit connects it would repaint in front of the user, which is the failure
    /// this setting exists to avoid.
    /// </remarks>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// Which title variant a sync writes to <see cref="Anime.Title"/>. Romaji by
    /// default, which is what a MyAnimeList library already holds. Changing it
    /// triggers a sync rather than swapping columns, so the title is rewritten
    /// through the same path that set it.
    /// </summary>
    public TitleLanguage PreferredTitleLanguage { get; set; } = TitleLanguage.Romaji;
}

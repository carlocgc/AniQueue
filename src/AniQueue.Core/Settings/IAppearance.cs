using AniQueue.Core.Domain;

namespace AniQueue.Core.Settings;

/// <summary>
/// The one preference that changes how the application looks rather than what it
/// does.
/// </summary>
/// <remarks>
/// Its own service rather than a pair of methods on something larger, because the
/// two callers have nothing else in common: the settings page writes it, and the
/// root component reads it once per page load to decide what the browser is sent.
///
/// It reads the database rather than <c>userconfig.json</c> because it describes
/// how AniQueue looks to one person, which is what <c>ProfileSettings</c> is for.
/// </remarks>
public interface IAppearance
{
    /// <summary>
    /// The stored theme, or <see cref="ThemePreference.System"/> when a profile has
    /// no settings row yet.
    /// </summary>
    Task<ThemePreference> GetThemeAsync(int profileId, CancellationToken cancellationToken = default);

    Task SaveThemeAsync(int profileId, ThemePreference theme, CancellationToken cancellationToken = default);
}

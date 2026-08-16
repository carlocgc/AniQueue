using System.Globalization;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>A link out to another site, ready to render.</summary>
public sealed record SourceLink(string Label, string Url);

/// <summary>
/// Builds links to the site a title was imported from.
///
/// This needs no lookup, no configuration and no network: <see cref="Anime.Source"/>
/// and <see cref="Anime.SourceAnimeId"/> are already stored by the importer, so the
/// URL is pure formatting.
///
/// It is also the first implementation of the pattern the Plex and Overseerr links
/// will use (ROADMAP.md §10) — given a title, return an optional link. Those need
/// a configured base URL, which is the only reason they are not here too.
/// </summary>
public static class SourceLinkBuilder
{
    /// <summary>
    /// A link to the title on the site it came from, or null for manual entries and
    /// for sources with no identifier to link to.
    /// </summary>
    public static SourceLink? ForAnime(AnimeSource source, string? sourceAnimeId)
    {
        if (string.IsNullOrWhiteSpace(sourceAnimeId))
        {
            return null;
        }

        // Identifiers come from imported files, so they are not trusted to be
        // numeric. Anything else is not a usable path segment on either site, and
        // refusing here means nothing has to be escaped downstream.
        if (!long.TryParse(sourceAnimeId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return source switch
        {
            AnimeSource.MyAnimeList => new SourceLink(
                "View on MyAnimeList",
                string.Format(CultureInfo.InvariantCulture, "https://myanimelist.net/anime/{0}", id)),

            AnimeSource.AniList => new SourceLink(
                "View on AniList",
                string.Format(CultureInfo.InvariantCulture, "https://anilist.co/anime/{0}", id)),

            // Manual entries were typed in here; there is nowhere to send the user.
            _ => null
        };
    }
}

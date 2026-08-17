using System.Globalization;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>
/// A link out to another site, ready to render.
/// </summary>
/// <param name="ShortName">
/// What the badge shows — "MAL", "AniList". Short because it sits inline with the
/// other row badges and a sentence there reads as prose rather than a control.
/// </param>
/// <param name="SiteName">
/// The site spelled out, for the accessible name and tooltip. "MAL" alone is
/// jargon, and a screen reader announcing it would be worse than useless.
/// </param>
/// <param name="Url">Where it goes.</param>
public sealed record SourceLink(string ShortName, string SiteName, string Url)
{
    /// <summary>Accessible name, e.g. "Open Golden Boy on MyAnimeList".</summary>
    public string DescribeFor(string title) => $"Open {title} on {SiteName}";
}

/// <summary>
/// Builds links to the sites that identify a title.
///
/// This needs no lookup, no configuration and no network: the identifiers are
/// already stored by the importer, so a URL is pure formatting.
///
/// It is also the first implementation of the pattern the Plex and Overseerr links
/// will use (ROADMAP.md §10) — given a title, return an optional link. Those need
/// a configured base URL, which is the only reason they are not here too.
/// </summary>
public static class SourceLinkBuilder
{
    /// <summary>
    /// A link per identifier that points somewhere, in a stable order.
    /// </summary>
    /// <remarks>
    /// Returns several since D17: a title AniList knows carries a MyAnimeList
    /// identifier too, and offering both is strictly more useful than picking one.
    /// Ordered by source so a row's badges do not reshuffle between renders.
    /// </remarks>
    public static IReadOnlyList<SourceLink> ForAnime(IEnumerable<ExternalIdentifier>? identifiers)
    {
        if (identifiers is null)
        {
            return [];
        }

        return [.. identifiers
            .OrderBy(i => i.Source)
            .Select(i => For(i.Source, i.Value))
            .OfType<SourceLink>()];
    }

    /// <summary>
    /// A link for one identifier, or null when the source or the value cannot
    /// produce one.
    /// </summary>
    public static SourceLink? For(AnimeSource source, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        // Identifiers come from imported files, so they are not trusted to be
        // numeric. Anything else is not a usable path segment on either site, and
        // refusing here means nothing has to be escaped downstream.
        if (!long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return source switch
        {
            AnimeSource.MyAnimeList => new SourceLink(
                "MAL",
                "MyAnimeList",
                string.Format(CultureInfo.InvariantCulture, "https://myanimelist.net/anime/{0}", id)),

            AnimeSource.AniList => new SourceLink(
                "AniList",
                "AniList",
                string.Format(CultureInfo.InvariantCulture, "https://anilist.co/anime/{0}", id)),

            // Manual entries were typed in here; there is nowhere to send the user.
            _ => null
        };
    }
}

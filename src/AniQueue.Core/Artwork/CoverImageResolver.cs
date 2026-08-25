using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AniQueue.Core.Artwork;

/// <summary>
/// What a row should actually render for a title's art.
/// </summary>
/// <param name="Url">The served image, or null when nothing is cached.</param>
/// <param name="Colour">
/// A validated <c>#rrggbb</c> to fill the space with, or null when the source never
/// published one.
/// </param>
public readonly record struct CoverArt(string? Url, string? Colour)
{
    public bool HasImage => Url is not null;

    public bool HasColour => Colour is not null;

    /// <summary>True when there is nothing to show at all — roughly one title in thirteen.</summary>
    public bool IsEmpty => Url is null && Colour is null;
}

/// <summary>
/// Turns what the database knows about a title's art into what the page renders
/// (D47).
///
/// Pure formatting, like <see cref="Library.SourceLinkBuilder"/> and for the same
/// reason: the inputs are already loaded by the query the page is running anyway, so
/// this needs no lookup, no configuration and no network.
/// </summary>
/// <remarks>
/// <b>§5 called this <c>ICoverImageResolver</c>.</b> It is a static builder instead,
/// because there is one implementation, no seam anything would substitute at, and
/// nothing to inject — the same conclusion <c>SourceLinkBuilder</c> reached. The
/// interface would have been a name with no behaviour behind it.
/// </remarks>
public static class CoverImageResolver
{
    /// <summary>The route the endpoint serves cached art from.</summary>
    /// <remarks>
    /// Held here rather than in the endpoint so the two cannot disagree: this builds
    /// the URL and the endpoint answers it, and a mismatch would be a page of broken
    /// images that compiled cleanly.
    /// </remarks>
    public const string RoutePrefix = "covers";

    /// <summary>
    /// The art for one title, given its cached image row and its dominant colour.
    /// </summary>
    /// <remarks>
    /// <b>The URL carries the content hash, and that is what makes it cacheable
    /// forever.</b> Replacing a title's art changes AniList's URL, which re-fetches,
    /// which changes the hash, which changes this address — so the endpoint can send
    /// a year's <c>max-age</c> with <c>immutable</c> and a fifty-row page spends no
    /// requests revalidating images it already has.
    /// </remarks>
    public static CoverArt ForAnime(int animeId, string? contentHash, string? colour)
    {
        var url = string.IsNullOrWhiteSpace(contentHash)
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"/{RoutePrefix}/{animeId}/{contentHash}");

        return new CoverArt(url, Palette(colour));
    }

    /// <summary>
    /// The colour, but only if it is one that can safely reach a style attribute.
    /// </summary>
    /// <remarks>
    /// This value is published by AniList and ends up in inline CSS. Blazor escapes
    /// the attribute, so it cannot break out of it — but it cannot stop
    /// <c>red;background-image:url(…)</c> being a valid declaration inside one, and
    /// that would be a third party choosing what a self-hosted page fetches. Six
    /// hexadecimal digits behind a hash is the entire shape AniList publishes, so
    /// requiring exactly that costs nothing and closes it.
    /// </remarks>
    private static string? Palette([NotNullWhen(true)] string? colour)
    {
        if (colour is not { Length: 7 } || colour[0] != '#')
        {
            return null;
        }

        for (var i = 1; i < colour.Length; i++)
        {
            if (!Uri.IsHexDigit(colour[i]))
            {
                return null;
            }
        }

        return colour;
    }
}

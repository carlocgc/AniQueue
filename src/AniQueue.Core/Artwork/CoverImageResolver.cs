using AniQueue.Core.Domain;

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
///
/// Where the picture actually is belongs to <see cref="ArtworkPaths"/>, which the job
/// and the endpoint share. This decides only <i>what to show</i>.
/// </remarks>
public static class CoverImageResolver
{
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
    public static CoverArt ForAnime(
        int animeId,
        string? contentHash,
        string? fileExtension,
        string? colour,
        ImageKind kind = ImageKind.Poster,
        ImageRendition rendition = ImageRendition.Thumbnail)
    {
        var url = contentHash is { Length: > 0 } hash && fileExtension is { Length: > 0 } extension
            ? ArtworkPaths.Url(kind, rendition, animeId, hash, extension)
            : null;

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
    private static string? Palette(string? colour)
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

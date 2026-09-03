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
/// Turns what the database knows about a title's art into what the page renders.
/// Pure formatting over values the page's own query already loaded, so it needs no
/// lookup, no configuration and no network. Where the picture actually is belongs
/// to <see cref="ArtworkPaths"/>.
/// </summary>
public static class CoverImageResolver
{
    /// <summary>
    /// The art for one title, given its cached image row and its dominant colour.
    /// The URL carries the content hash, so replaced art arrives at a new address
    /// and the endpoint can cache it forever.
    /// </summary>
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
    /// the attribute but cannot stop <c>red;background-image:url(…)</c> being a
    /// valid declaration inside one, so anything that is not six hexadecimal digits
    /// behind a hash is dropped.
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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Artwork;

/// <summary>
/// Where a cached picture lives, on disk and on the wire. The fetch job, the
/// serving endpoint and the pages that build image addresses all go through here,
/// so a URL is the disk path with the separators in different places.
/// </summary>
/// <remarks>
/// A directory per rendition under one <c>art</c> root, so that deleting one
/// reclaims space without blanking the other until the job catches up.
/// </remarks>
public static class ArtworkPaths
{
    /// <summary>The route and the directory art is served from and cached under.</summary>
    public const string Root = "art";

    /// <summary>
    /// The directory a picture lives in — <c>thumbnails</c>, <c>posters</c>.
    /// Spelled out rather than derived from the enum names, so renaming a directory
    /// is not a data migration.
    /// </summary>
    public static string DirectoryFor(ImageKind kind, ImageRendition rendition) => (kind, rendition) switch
    {
        (ImageKind.Poster, ImageRendition.Thumbnail) => "thumbnails",
        (ImageKind.Poster, ImageRendition.Full) => "posters",
        (ImageKind.Banner, _) => "banners",
        (ImageKind.ClearLogo, _) => "logos",
        (ImageKind.Backdrop, _) => "backdrops",

        // A throw rather than a fallback: a picture quietly filed under "other"
        // would be one nothing could find again.
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "No directory is defined for this image kind and rendition.")
    };

    /// <summary>
    /// The picture a directory segment names, if it names one. A whitelist, because
    /// this value arrives in a request and becomes a path component.
    /// </summary>
    public static bool TryParseDirectory(string? directory, out ImageKind kind, out ImageRendition rendition)
    {
        rendition = ImageRendition.Full;

        switch (directory)
        {
            case "thumbnails": kind = ImageKind.Poster; rendition = ImageRendition.Thumbnail; return true;
            case "posters": kind = ImageKind.Poster; return true;
            case "banners": kind = ImageKind.Banner; return true;
            case "logos": kind = ImageKind.ClearLogo; return true;
            case "backdrops": kind = ImageKind.Backdrop; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// What the cached file is called within its directory. The hash is what makes
    /// the served address change when the bytes change, which is what lets the
    /// endpoint send a year's <c>max-age</c>.
    /// </summary>
    public static string CacheFileName(int animeId, string contentHash, string fileExtension) =>
        string.Create(CultureInfo.InvariantCulture, $"{animeId}-{contentHash}{fileExtension}");

    /// <summary>
    /// The path within the cache root, relative and slash-separated so it compares
    /// against what the sweep finds on disk on either operating system.
    /// </summary>
    public static string RelativePath(
        ImageKind kind,
        ImageRendition rendition,
        int animeId,
        string contentHash,
        string fileExtension) =>
        $"{DirectoryFor(kind, rendition)}/{CacheFileName(animeId, contentHash, fileExtension)}";

    /// <summary>Where the page points an <c>img</c> at this picture.</summary>
    public static string Url(
        ImageKind kind,
        ImageRendition rendition,
        int animeId,
        string contentHash,
        string fileExtension) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"/{Root}/{DirectoryFor(kind, rendition)}/{animeId}/{contentHash}{fileExtension}");

    /// <summary>
    /// Splits the last URL segment into a hash and an extension, if it is one this
    /// application could have produced.
    /// </summary>
    /// <remarks>
    /// The path-safety gate. This segment arrives from a request, so it accepts only
    /// hexadecimal followed by a known image extension rather than stripping what
    /// looks dangerous: a traversal sequence, a separator, a drive letter and a null
    /// byte all fail the same check.
    /// </remarks>
    public static bool TryParseSegment(
        string? segment,
        [NotNullWhen(true)] out string? contentHash,
        [NotNullWhen(true)] out string? fileExtension)
    {
        contentHash = null;
        fileExtension = null;

        if (segment is null)
        {
            return false;
        }

        var dot = segment.LastIndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        var hash = segment[..dot];
        var extension = segment[dot..];

        if (hash.Length is 0 or > 64 || ImageSource.ContentTypeFor(extension) is null)
        {
            return false;
        }

        foreach (var character in hash)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        contentHash = hash;
        fileExtension = extension;
        return true;
    }
}

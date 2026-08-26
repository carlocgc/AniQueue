using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Artwork;

/// <summary>
/// Where a cached picture lives, on disk and on the wire (D47).
/// </summary>
/// <remarks>
/// <b>One place, because two would drift.</b> The job writes files, the endpoint
/// reads them and the page builds the addresses, and none of the three ever speaks to
/// the others. If they disagreed about a name, every picture would cache successfully
/// and none would ever be served — so all three go through here and the URL is the
/// disk path with separators in different places.
///
/// <b>A directory per kind, under one <c>art</c> root.</b> Phase 9b turns 810 files
/// into some four thousand across four kinds, and one directory holding all of them
/// is worse to list, worse to sweep and hides what a file actually is. The kinds sit
/// under a single root rather than at the top of <c>/data</c> so the volume gains one
/// entry beside the database rather than four.
/// </remarks>
public static class ArtworkPaths
{
    /// <summary>The route and the directory art is served from and cached under.</summary>
    public const string Root = "art";

    /// <summary>
    /// The directory a kind's pictures live in — <c>posters</c>, <c>banners</c>.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived from the enum name, because the two answer to
    /// different masters: the enum is a data contract whose members must never be
    /// reordered or renamed, and these are directory names a person reads. Tying them
    /// together would make renaming a directory a data migration.
    /// </remarks>
    public static string DirectoryFor(ImageKind kind) => kind switch
    {
        ImageKind.Poster => "posters",
        ImageKind.Banner => "banners",
        ImageKind.ClearLogo => "logos",
        ImageKind.Backdrop => "backdrops",

        // Unreachable while the enum and this switch agree, and a throw rather than a
        // fallback because a kind quietly filed under "other" would be a picture
        // nothing could ever find again.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No directory is defined for this image kind.")
    };

    /// <summary>The kind a directory segment names, if it names one.</summary>
    /// <remarks>
    /// A whitelist, like <see cref="TryParseSegment"/> and for the same reason: this
    /// value arrives in a request and becomes a path component, so it is matched
    /// against the four names that exist rather than checked for anything dangerous.
    /// </remarks>
    public static bool TryParseKind(string? directory, out ImageKind kind)
    {
        switch (directory)
        {
            case "posters": kind = ImageKind.Poster; return true;
            case "banners": kind = ImageKind.Banner; return true;
            case "logos": kind = ImageKind.ClearLogo; return true;
            case "backdrops": kind = ImageKind.Backdrop; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// What the cached file is called within its kind's directory.
    /// </summary>
    /// <remarks>
    /// <b>The hash is not decoration and does not come out.</b> It is what makes the
    /// served address change when the bytes change, which is the whole reason the
    /// endpoint may send a year's <c>max-age</c> with <c>immutable</c> and a fifty-row
    /// page may spend no requests revalidating. A name derived from the title instead
    /// would leave replaced art unreachable behind a cached copy for up to a year —
    /// §10's "the URLs rot", moved from AniList's CDN onto ours — and would put a
    /// third party's string into a path, which is exactly what the parser below makes
    /// impossible today.
    /// </remarks>
    public static string CacheFileName(int animeId, string contentHash, string fileExtension) =>
        string.Create(CultureInfo.InvariantCulture, $"{animeId}-{contentHash}{fileExtension}");

    /// <summary>The path within the cache root, using forward slashes.</summary>
    /// <remarks>
    /// Relative and slash-separated so it can be compared against what the sweep finds
    /// on disk on either operating system without either side normalising.
    /// </remarks>
    public static string RelativePath(ImageKind kind, int animeId, string contentHash, string fileExtension) =>
        $"{DirectoryFor(kind)}/{CacheFileName(animeId, contentHash, fileExtension)}";

    /// <summary>Where the page points an <c>img</c> at this picture.</summary>
    public static string Url(ImageKind kind, int animeId, string contentHash, string fileExtension) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"/{Root}/{DirectoryFor(kind)}/{animeId}/{contentHash}{fileExtension}");

    /// <summary>
    /// Splits the last URL segment into a hash and an extension, if it is one this
    /// application could have produced.
    /// </summary>
    /// <remarks>
    /// <b>The path-safety gate, and a whitelist rather than a sanitiser.</b> §6 forbids
    /// user-supplied file paths and this segment arrives from a request, so instead of
    /// stripping what looks dangerous it accepts only hexadecimal followed by a known
    /// image extension. A traversal sequence, a separator, a drive letter and a null
    /// byte all fail the same check, because none of them is a hexadecimal digit.
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

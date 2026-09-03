namespace AniQueue.Core.Artwork;

/// <summary>
/// What AniQueue is willing to fetch a picture from, and what it will accept back.
/// A cover address arrives inside an AniList response, so the host set is a
/// constant held in code and only the path comes from data — neither a setting
/// nobody has a reason to change, nor a third party choosing what a self-hosted
/// server connects to.
/// </summary>
public static class ImageSource
{
    /// <summary>
    /// Hosts a picture may be fetched from. Suffix-matched, so subdomains count:
    /// AniList has served covers from <c>s1</c> through <c>s4</c> over the years,
    /// and the thing being defended against is an arbitrary host rather than an
    /// unexpected one belonging to the same people.
    /// </summary>
    private static readonly string[] AllowedHostSuffixes = ["anilist.co"];

    /// <summary>
    /// The largest picture worth accepting. Two orders of magnitude above the
    /// largest cover AniList publishes, so nothing legitimate is refused and a
    /// malfunctioning endpoint cannot fill the disk one response at a time.
    /// </summary>
    public const long MaxByteCount = 5 * 1024 * 1024;

    /// <summary>
    /// Whether this is an address AniQueue will connect to. https only, so nothing
    /// between the container and the CDN can choose what lands in the cache and gets
    /// served back under AniQueue's own origin.
    /// </summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && IsAllowedHost(parsed.Host);

    private static bool IsAllowedHost(string host)
    {
        foreach (var suffix in AllowedHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (host.Length > suffix.Length
                && host[^(suffix.Length + 1)] == '.'
                && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The file extension for a content type, or null when it is not a picture.
    /// What the server said it sent decides, never the URL's path. A type not on
    /// this list is a permanent failure rather than a retry.
    /// </summary>
    public static string? ExtensionFor(string? contentType) =>
        Normalise(contentType) switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => null
        };

    /// <summary>The content type to serve a cached file back as.</summary>
    public static string? ContentTypeFor(string? extension) =>
        extension switch
        {
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };

    /// <summary>Drops the charset and whitespace a header is allowed to carry.</summary>
    private static string? Normalise(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var type = separator < 0 ? contentType : contentType[..separator];

        return type.Trim().ToLowerInvariant();
    }
}

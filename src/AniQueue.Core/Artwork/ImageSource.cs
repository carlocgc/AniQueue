namespace AniQueue.Core.Artwork;

/// <summary>
/// What AniQueue is willing to fetch a picture from, and what it will accept back
/// (D47, §6).
/// </summary>
/// <remarks>
/// <b>This is §6's rule surviving rather than gaining an exception.</b> That rule —
/// every endpoint the application reaches on its own initiative is a constant held in
/// code — was written before anything had to fetch a URL somebody else published. A
/// cover address arrives inside an AniList response, so it is neither a constant nor
/// user input, and the two obvious answers were both wrong: making it configurable
/// turns an attack surface into a setting nobody has a reason to change, and
/// accepting whatever the API names lets a third party choose what a self-hosted
/// server connects to.
///
/// So the <i>host set</i> is the constant and only the path comes from data. Every
/// one of the 810 covers in the development library is on one host over https, which
/// is what makes this cheap; Phase 9b adds the TVDB and TMDB image hosts to the list
/// and changes nothing else about it.
///
/// Pure and here rather than beside the fetcher because these are the checks worth
/// testing exhaustively, and none of them needs a socket to decide.
/// </remarks>
public static class ImageSource
{
    /// <summary>
    /// Hosts a picture may be fetched from. Suffix-matched, so subdomains count.
    /// </summary>
    /// <remarks>
    /// AniList serves covers from <c>s4.anilist.co</c> today and has used
    /// <c>s1</c> through <c>s4</c> over the years, so the registrable domain is the
    /// honest unit — pinning the exact host would break the day they add a server,
    /// and the thing being defended against is an arbitrary host rather than an
    /// unexpected one belonging to the same people.
    /// </remarks>
    private static readonly string[] AllowedHostSuffixes = ["anilist.co"];

    /// <summary>
    /// The largest picture worth accepting.
    /// </summary>
    /// <remarks>
    /// A cover at the size actually requested measured 11 KB on average as a JPEG and
    /// 30 KB as a PNG across a real 810-title library, with the largest at 80 KB, and
    /// the biggest size AniList publishes is under 100 KB. So this is two orders of
    /// magnitude of headroom —
    /// sized the way §6 sizes the import limit, generously enough that nothing
    /// legitimate is refused and tightly enough that a malfunctioning or hostile
    /// endpoint cannot fill the disk one response at a time.
    /// </remarks>
    public const long MaxByteCount = 5 * 1024 * 1024;

    /// <summary>
    /// Whether this is an address AniQueue will connect to.
    /// </summary>
    /// <remarks>
    /// https only. Not because a cover is a secret, but because the alternative lets
    /// anything between the container and the CDN choose what lands in the cache and
    /// gets served back under AniQueue's own origin.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    /// <b>What the server said it sent decides, never the URL's path.</b> The path is
    /// the part a third party controls, and a body's real type is not something a
    /// suffix can promise. A type not on this list is a permanent failure rather than
    /// a retry: a URL serving HTML will still be serving HTML in fifteen minutes.
    ///
    /// The extension is not decoration either — it travels in the served URL, which
    /// is what lets the endpoint answer without a database lookup per image on a page
    /// carrying fifty of them.
    /// </remarks>
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

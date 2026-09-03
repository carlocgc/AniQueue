namespace AniQueue.Core.Domain;

/// <summary>
/// One picture of one title, from one source: where it can be fetched from,
/// whether it has been, and what to serve it as.
/// </summary>
/// <remarks>
/// The bytes are not here. The file lives under <c>&lt;data&gt;/art/{directory}/</c>
/// — <c>thumbnails</c> or <c>posters</c>, one per rendition — and this row records
/// where it came from and what happened. Disk is the authority on whether it is
/// actually cached: the fetch job asks the filesystem as well as this row, so
/// deleting the cache directory heals within a tick.
/// </remarks>
public class AnimeImage
{
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public ImageKind Kind { get; set; }

    /// <summary>Who published this picture. Not who published the title.</summary>
    public AnimeSource Source { get; set; }

    /// <summary>
    /// Which size of this picture the row holds — a thumbnail for a list slot, a
    /// full-size cover for the detail dialog. Separate rows rather than extra
    /// columns so each rendition is fetched, retried and cached on its own.
    /// </summary>
    public ImageRendition Rendition { get; set; }

    /// <summary>
    /// Where the picture is, as the source published it, and the invalidation key:
    /// AniList URLs carry a content hash, so replaced art arrives at a new address
    /// and a change here clears both failure states and re-fetches.
    /// </summary>
    /// <remarks>
    /// The host is checked against a constant allowlist before any request is made.
    /// </remarks>
    public required string RemoteUrl { get; set; }

    /// <summary>
    /// The hash of the cached bytes, or null while nothing has been cached. Also
    /// what makes the served URL immutable, so it can carry a year's
    /// <c>max-age</c>.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// The URL the cached bytes actually came from, or null while none have.
    /// Outstanding work is this disagreeing with <see cref="RemoteUrl"/>, which
    /// covers both a title never fetched and one whose art has been replaced.
    /// </summary>
    public string? FetchedUrl { get; set; }

    /// <summary>
    /// The extension the cached file was written with, taken from the response
    /// content type rather than from the URL path, which a third party controls.
    /// </summary>
    public string? FileExtension { get; set; }

    public long? ByteCount { get; set; }

    /// <summary>When the cached file arrived, or null while it has not.</summary>
    public DateTimeOffset? FetchedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>
    /// True when the failure was about the picture rather than the network — a 404,
    /// a body that is not an image, one over the size cap, or a host that is not on
    /// the allowlist. Those will not change while the URL stays the same, so they
    /// are not retried. A timeout, a 5xx, a 429 or a dropped connection get
    /// <see cref="AttemptCount"/> tries.
    /// </summary>
    public bool FailureIsPermanent { get; set; }

    /// <summary>
    /// Transient failures so far for this URL. Reset when the URL changes. Bounds
    /// how often an unreachable title is asked about.
    /// </summary>
    public int AttemptCount { get; set; }
}

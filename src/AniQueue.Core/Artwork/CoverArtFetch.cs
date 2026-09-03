namespace AniQueue.Core.Artwork;

/// <summary>
/// How an attempt to fetch one picture ended. Three outcomes rather than two,
/// because the difference between the two failures is the whole retry policy, and
/// it is decided once where the response is read rather than by every caller
/// inspecting a status code again.
/// </summary>
public enum CoverArtFetchStatus
{
    /// <summary>The bytes arrived and are a picture.</summary>
    Fetched,

    /// <summary>
    /// Something true about this URL rather than about this moment: a 404, a body
    /// that is not an image, one over the cap, a redirect, or a host that is not on
    /// the allowlist. Only the URL changing makes the question worth asking again.
    /// </summary>
    PermanentlyUnavailable,

    /// <summary>
    /// Something true about this moment rather than about this URL — a timeout, a
    /// 5xx, a 429, a dropped connection.
    /// </summary>
    TemporarilyUnavailable
}

/// <summary>One picture, or the reason there is not one.</summary>
public sealed record CoverArtFetch(
    CoverArtFetchStatus Status,
    byte[]? Content = null,
    string? FileExtension = null)
{
    public static readonly CoverArtFetch Permanent = new(CoverArtFetchStatus.PermanentlyUnavailable);

    public static readonly CoverArtFetch Transient = new(CoverArtFetchStatus.TemporarilyUnavailable);

    public static CoverArtFetch Success(byte[] content, string fileExtension) =>
        new(CoverArtFetchStatus.Fetched, content, fileExtension);
}

/// <summary>
/// Fetches one picture from an address AniQueue is willing to reach. An interface
/// so the guards can be tested against a stub transport with no database near them.
/// </summary>
public interface ICoverArtClient
{
    Task<CoverArtFetch> FetchAsync(string remoteUrl, CancellationToken cancellationToken);
}

namespace AniQueue.Core.Import;

/// <summary>
/// Bounds on what an import is allowed to consume. Uploads are attacker-controlled
/// in the general case, and an unbounded parse is a denial-of-service waiting to
/// happen even on a single-user home server.
/// </summary>
public sealed record ImportLimits
{
    public static ImportLimits Default { get; } = new();

    /// <summary>
    /// 32 MB. A MyAnimeList export of several thousand titles is a few megabytes,
    /// so this leaves generous headroom while still refusing an absurd file.
    /// </summary>
    public int MaxBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// Caps records even if the file is small — a compact file can still describe
    /// an unreasonable number of entries.
    /// </summary>
    public int MaxEntries { get; init; } = 50_000;
}

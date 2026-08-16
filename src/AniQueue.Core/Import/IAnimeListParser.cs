namespace AniQueue.Core.Import;

/// <summary>
/// Turns an import file into normalised entries. Implementations are pure: no
/// database, no configuration, no I/O beyond the supplied stream (D9).
///
/// This is the extension point for new formats. Adding AniList means adding one
/// implementation here — matching, preview and commit are format-agnostic and do
/// not change.
/// </summary>
public interface IAnimeListParser
{
    /// <summary>Human-readable format name, shown in the import UI.</summary>
    string FormatName { get; }

    /// <summary>
    /// Parses the stream. Never throws for malformed input — problems are returned
    /// in the result, because a partially broken file is a normal thing for a user
    /// to upload and an exception would lose the records that were fine.
    /// </summary>
    Task<ParseResult> ParseAsync(Stream input, CancellationToken cancellationToken = default);
}

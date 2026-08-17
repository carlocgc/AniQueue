using AniQueue.Core.Progress;

namespace AniQueue.Core.Import;

/// <summary>The outcome of actually committing a previewed import.</summary>
public sealed record ImportCommitResult
{
    public required int Created { get; init; }

    public required int Updated { get; init; }

    public required int Skipped { get; init; }

    /// <summary>
    /// Slots that left Up Next because the import showed their titles are no longer
    /// waiting to be watched (D12).
    /// </summary>
    /// <remarks>
    /// Reported rather than left silent. The queue shortening on its own is the
    /// intended behaviour, but a user who does not know the rule exists would read
    /// it as an import having eaten their ordering.
    /// </remarks>
    public int QueueSlotsReleased { get; init; }

    public int Total => Created + Updated + Skipped;
}

/// <summary>
/// Matches parsed entries against the existing library and applies them.
///
/// Deliberately two calls rather than one: <see cref="PreviewAsync"/> only reads,
/// and nothing is written until <see cref="CommitAsync"/> is called with a preview
/// the user has seen and accepted.
/// </summary>
public interface IImportService
{
    /// <summary>
    /// Works out what an already-parsed payload would do. Reads the library;
    /// writes nothing.
    /// </summary>
    /// <remarks>
    /// This is the primitive, and the stream overload composes onto it. A sync has
    /// already fetched — possibly across several responses, merged into one
    /// <see cref="ParseResult"/> — so it has no single stream to hand over, and
    /// without this seam it would need a parallel matching path. Keeping one means
    /// the difference between an upload and a sync is the trigger, not the logic.
    /// </remarks>
    Task<ImportPreview> PreviewAsync(
        ParseResult parsed,
        string formatName,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a stream and works out what importing it would do. Reads the library;
    /// writes nothing.
    /// </summary>
    /// <param name="progress">
    /// Optional. Reports stages as they happen so the caller can show something
    /// truthful rather than an indeterminate spinner.
    /// </param>
    Task<ImportPreview> PreviewAsync(
        Stream input,
        IAnimeListParser parser,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a previously generated preview inside a single transaction.
    ///
    /// Only <see cref="ImportAction.Create"/> and <see cref="ImportAction.Update"/>
    /// items are applied; conflicts and unchanged rows are counted as skipped.
    /// Local curation — queue position, notes, franchise membership, hidden flag
    /// and recommendation data — is never overwritten by an import.
    /// </summary>
    Task<ImportCommitResult> CommitAsync(
        ImportPreview preview,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

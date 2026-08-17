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
    /// Works out what the import would do. Reads the library; writes nothing.
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

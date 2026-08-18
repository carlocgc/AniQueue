using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Sync;

/// <summary>What a fetch produced: something to review, or a reason it did not happen.</summary>
public sealed record SyncFetchResult
{
    public required AnimeSource Source { get; init; }

    /// <summary>Null when the fetch failed.</summary>
    public ImportPreview? Preview { get; init; }

    /// <summary>Null when the fetch succeeded. Plain words; shown as-is.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// True when the run is already over — it failed, or the list already matched
    /// the library — so the caller has a result to report rather than a decision to
    /// ask for.
    /// </summary>
    /// <remarks>
    /// Conflicts count as unfinished even though committing them would change
    /// nothing by default. They are the one thing here a person has to answer, and
    /// treating "nothing will happen unless you decide" as a completed sync is how
    /// a pending decision becomes invisible.
    /// </remarks>
    public bool IsComplete =>
        FailureReason is not null || Preview is { HasApplicableChanges: false, ConflictCount: 0 };

    public bool Succeeded => FailureReason is null;
}

/// <summary>What applying a reviewed sync did.</summary>
public sealed record SyncApplyResult
{
    public required ImportCommitResult Commit { get; init; }

    /// <summary>Conflicts the user left unresolved, which stay pending for next time.</summary>
    public required int ConflictsHeld { get; init; }
}

/// <summary>How one source stands right now, for the Sources page.</summary>
public sealed record SourceSyncStatus
{
    public required AnimeSource Source { get; init; }

    public required SourceSyncSettings Settings { get; init; }

    /// <summary>Whether an account has been configured for this source (D20).</summary>
    public required bool IsConfigured { get; init; }

    /// <summary>The account being read, for display. Never a credential — a public username.</summary>
    public string? Account { get; init; }

    /// <summary>The most recent completed run, or null if this source has never finished one.</summary>
    public SyncRun? LastRun { get; init; }
}

/// <summary>
/// Runs a source's fetch through the import pipeline (§5).
///
/// The difference between an upload and a sync is the trigger, not the logic: this
/// fetches, parses and hands the result to <see cref="IImportService"/> for exactly
/// the same matching, preview, commit and queue advancement a file gets. Nothing
/// about reconciliation is duplicated here.
///
/// Two calls rather than one, for the same reason import has two: in Phase 5b every
/// run is user-initiated, and the preview is the review surface (D21). Phase 5c's
/// unattended runner calls both in sequence without a human between them.
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// Fetches the source's list and works out what applying it would do. Writes
    /// nothing to the library.
    /// </summary>
    /// <remarks>
    /// A <see cref="SyncRun"/> is recorded here only when the run is already over —
    /// a failure, or a list that already matches. A preview waiting on a human has
    /// not finished, and recording it would let the page claim the library is up to
    /// date while the changes sit unconfirmed on screen.
    /// </remarks>
    Task<SyncFetchResult> FetchAsync(
        int profileId,
        AnimeSource source,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a preview the user has reviewed, and records the run.
    /// </summary>
    Task<SyncApplyResult> ApplyAsync(
        ImportPreview preview,
        int profileId,
        AnimeSource source,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the state of every syncable source: its settings, whether an account
    /// is configured for it, and how its last run ended.
    /// </summary>
    Task<IReadOnlyList<SourceSyncStatus>> GetStatusAsync(
        int profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores this profile's settings for one source, creating the row if this is
    /// the first time the user has said anything about it.
    /// </summary>
    Task SaveSettingsAsync(SourceSyncSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the preferred title language (D22).
    /// </summary>
    /// <remarks>
    /// A profile-wide preference living behind the sync service, because a sync is
    /// the only thing that acts on it and the Sources page is where it is set until
    /// Phase 10 builds a settings page. Changing it does not rewrite anything: the
    /// next fetch does, through the same path that wrote the titles originally.
    /// </remarks>
    Task SavePreferredTitleLanguageAsync(
        int profileId,
        TitleLanguage language,
        CancellationToken cancellationToken = default);

    /// <summary>The preferred title language, for rendering the control that sets it.</summary>
    Task<TitleLanguage> GetPreferredTitleLanguageAsync(
        int profileId,
        CancellationToken cancellationToken = default);
}

using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Settings;
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
    /// Titles this source has stopped listing, marked during the fetch.
    /// </summary>
    /// <remarks>
    /// Already written by the time this is read, unlike everything else on a
    /// preview. Absence is an observation about the response rather than a change
    /// the user is being asked to approve, and it has to be recorded where it is
    /// observed: a fetch whose list is otherwise identical has nothing to apply, so
    /// deferring the mark to the commit would mean the one case absence exists for
    /// never records it.
    /// </remarks>
    public int AbsentFlagged { get; init; }

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

/// <summary>What one unattended run did, for the log and the staleness notice.</summary>
/// <remarks>
/// Deliberately not <see cref="SyncRun"/> itself. The row is the audit trail and
/// belongs to the database; this is the answer to "did anything happen just now",
/// which is what the runner logs and what decides whether an open page is told it
/// has gone stale.
/// </remarks>
public sealed record UnattendedSyncResult
{
    public required AnimeSource Source { get; init; }

    /// <summary>Null when the run was not due, or was refused before it started.</summary>
    public SyncOutcome? Outcome { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    public int SlotsReleased { get; init; }

    public int AbsentFlagged { get; init; }

    /// <summary>Unambiguous changes found and not applied, because this source asks first.</summary>
    public int ChangesHeld { get; init; }

    public int ConflictsHeld { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>Whether an open page is now showing something that is no longer true.</summary>
    public bool ChangedLibrary => Created + Updated + SlotsReleased + AbsentFlagged > 0;

    /// <summary>The run did not happen: not due, switched off, or nothing configured.</summary>
    public static UnattendedSyncResult NotRun(AnimeSource source) => new() { Source = source };
}

/// <summary>How one source stands right now, for the Sources page.</summary>
public sealed record SourceSyncStatus
{
    public required AnimeSource Source { get; init; }

    public required SourceSyncSettings Settings { get; init; }

    /// <summary>
    /// Whether this source has a list AniQueue can go and read, or only a file the
    /// user brings.
    /// </summary>
    /// <remarks>
    /// The one real difference between the two sources on the page, and the reason
    /// they can otherwise share a card. Everything else about them — ranking,
    /// what a preview looks like, what applying one does — is identical, so the
    /// split is a fetch button against a file picker rather than two pages.
    ///
    /// A file source is never scheduled, never stalls and never reports a failure,
    /// because nothing runs on its behalf.
    /// </remarks>
    public required bool CanFetch { get; init; }

    /// <summary>
    /// Whether this source outranks the others when two of them describe one title.

    /// </summary>
    /// <remarks>
    /// A flag rather than a rank compared against a constant. The seat is single, so
    /// naming its occupant in one setting says that directly, where a rank per source
    /// could represent two primaries or none.
    /// </remarks>
    public required bool IsPrimary { get; init; }

    /// <summary>Whether an account has been configured for this source.</summary>
    public required bool IsConfigured { get; init; }

    /// <summary>The account being read, for display. Never a credential — a public username.</summary>
    public string? Account { get; init; }

    /// <summary>How many of this profile's titles the source has stopped listing.</summary>
    public int AbsentCount { get; init; }

    /// <summary>
    /// A few of those titles by name, so the notice can be specific rather than
    /// numeric. Capped: this is a reminder to go and look, not a report.
    /// </summary>
    public IReadOnlyList<string> AbsentTitles { get; init; } = [];

    /// <summary>The most recent completed run, or null if this source has never finished one.</summary>
    public SyncRun? LastRun { get; init; }

    /// <summary>
    /// The most recent run that reached the source, whether or not it changed
    /// anything. Null until one has.
    /// </summary>
    /// <remarks>
    /// Reported separately from <see cref="LastFailure"/> rather than folded into
    /// one "last run", because "last synced 3 hours ago, last attempt failed:
    /// profile is private" is actionable where "sync failed" is not — and a page
    /// that can only show the newer of the two either hides that it is broken or
    /// hides that it ever worked.
    /// </remarks>
    public SyncRun? LastSuccess { get; init; }

    /// <summary>The most recent failed run. Null until one fails.</summary>
    public SyncRun? LastFailure { get; init; }

    /// <summary>
    /// Failures since the last run that reached the source. Zero when the last run
    /// worked.
    /// </summary>
    /// <remarks>
    /// The whole of the backoff state, and deliberately derived rather than stored:
    /// a counter column would be a second copy of something <see cref="SyncRun"/>
    /// already records exactly, and one that could disagree with it.
    /// </remarks>
    public int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Whether sync for this source has been failing long enough to be worth
    /// interrupting the user about.
    /// </summary>
    /// <remarks>
    /// Three in a row rather than one, because one failure is a rate limit or a
    /// flaky connection and resolves itself. Three is a mistyped account, a list
    /// turned private, or a service that is genuinely down — none of which fix
    /// themselves, all of which mean Up Next is quietly running on old data.
    /// </remarks>
    public bool IsStalled => ConsecutiveFailures >= 3;
}

/// <summary>
/// Runs a source's fetch through the import pipeline.
///
/// The difference between an upload and a sync is the trigger, not the logic: this
/// fetches, parses and hands the result to <see cref="IImportService"/> for exactly
/// the same matching, preview, commit and queue advancement a file gets. Nothing
/// about reconciliation is duplicated here.
///
/// Two calls rather than one, for the same reason import has two: the preview is
/// the review surface for a run somebody asked for. The unattended runner calls both
/// in sequence with nobody between them.
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
    /// Applies a fetch the user has reviewed, and records the run.
    /// </summary>
    /// <remarks>
    /// Takes the whole fetch rather than its preview because the run record has to
    /// state everything the run did, and what a fetch observed about absence is not
    /// visible in the preview it produced.
    /// </remarks>
    Task<SyncApplyResult> ApplyAsync(
        SyncFetchResult fetch,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a source end to end with nobody present: fetches, applies what this
    /// profile's settings allow, holds the rest, and records the run.
    /// </summary>
    /// <remarks>
    /// Returns without doing anything — and without recording a run — when the
    /// source is not due, has no schedule, is switched off, or has no account
    /// configured. A log of runs that never ran would bury the failures that did.
    ///
    /// The safe subset is not computed here. <c>Create</c> and <c>Update</c> are
    /// already the unambiguous actions and <c>Conflict</c> is by definition not, so
    /// this decides only whether to commit that subset and what to do with the
    /// conflicts — there is no second opinion about what is safe.
    /// </remarks>
    Task<UnattendedSyncResult> RunUnattendedAsync(
        int profileId,
        AnimeSource source,
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
    /// <remarks>
    /// Returns the file write's result rather than <c>Task</c>, because this writes
    /// <c>userconfig.json</c> and a non-root container writing to a root-owned bind
    /// mount is a real deployment. A save that silently failed would leave a toggle
    /// showing the value it did not keep.
    /// </remarks>
    Task<UserSettingsSaveResult> SaveSettingsAsync(
        SourceSyncSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes one source primary and demotes every other, in one transaction.
    /// </summary>
    /// <remarks>
    /// Promotion rather than assignment, because primary is a single seat. A control
    /// that can express two primaries or none is a control that eventually will.
    ///
    /// There is deliberately no way to say "this one is not primary": demoting the
    /// only primary would leave nobody holding the seat. Somebody else is promoted
    /// instead, which is the same decision stated in the form that always leaves the
    /// setting meaningful.
    ///
    /// Rows are created for sources that have none, because a demotion has to be
    /// recorded to be worth anything — an absent row is a default, and defaults are
    /// what the promotion is overriding.
    /// </remarks>
    Task<UserSettingsSaveResult> SetPrimarySourceAsync(
        int profileId,
        AnimeSource source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the preferred title language.
    /// </summary>
    /// <remarks>
    /// A profile-wide preference living behind the sync service, because a sync is
    /// the only thing that acts on it. Changing it does not rewrite anything: the
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

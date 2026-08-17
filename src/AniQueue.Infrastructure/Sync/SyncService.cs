using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Progress;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Turns a remote list into an import.
///
/// Almost everything here is arrangement rather than logic, and that is the point:
/// the fetch is parsed into a <see cref="ParseResult"/> and handed to
/// <see cref="IImportService"/>, which does the matching, the preview, the commit
/// and the queue advancement exactly as it does for an uploaded file. If this class
/// ever grows a second opinion about how an entry matches, the seam has been lost.
///
/// What is genuinely its own: deciding whether a sync may run at all, and writing
/// the <see cref="SyncRun"/> that says how it went.
/// </summary>
public sealed class SyncService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IAniListClient aniListClient,
    AniListJsonParser aniListParser,
    IImportService importService,
    IOptionsMonitor<SyncOptions> options,
    ILogger<SyncService> logger) : ISyncService
{
    /// <summary>
    /// The sources a sync can actually read. MyAnimeList is absent deliberately —
    /// it is a file import, and nothing here fetches it.
    /// </summary>
    private static readonly AnimeSource[] SyncableSources = [AnimeSource.AniList];

    public async Task<SyncFetchResult> FetchAsync(
        int profileId,
        AnimeSource source,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireSyncable(source);

        var current = options.CurrentValue;

        // The kill switch, and no run is recorded for it. Nothing was attempted, and
        // a log of runs that never ran would bury the failures that did (D20).
        if (!current.Enabled)
        {
            return Failure(source, "Syncing is turned off in this deployment's configuration.");
        }

        var settings = await LoadSettingsAsync(profileId, source, cancellationToken);
        if (!settings.IsEnabled)
        {
            return Failure(source, $"{source} syncing is switched off for this profile.");
        }

        var account = current.AniList.UserName;
        if (string.IsNullOrWhiteSpace(account))
        {
            return Failure(source, "No AniList account is configured.");
        }

        var startedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Sync started for {Source}", source);

        progress?.Report(new OperationProgress($"Asking {source} for your list"));

        var fetch = await aniListClient.FetchListAsync(account, cancellationToken);
        if (!fetch.Succeeded)
        {
            logger.LogWarning("Sync fetch failed for {Source}: {Reason}", source, fetch.FailureReason);
            await RecordAsync(profileId, source, startedAt, Failed(fetch.FailureReason!), cancellationToken);
            return Failure(source, fetch.FailureReason!);
        }

        progress?.Report(new OperationProgress("Reading the response"));

        var parsed = await ParseAsync(profileId, fetch, cancellationToken);

        // A rejected parse is a fetch that cannot be trusted, not an empty list. The
        // distinction is the whole of D19's safety: an empty list means the user
        // deleted everything, and acting on that reading is unrecoverable.
        if (parsed.IsFileRejected)
        {
            var reason = parsed.Problems.Count > 0
                ? parsed.Problems[0].Message
                : "The response could not be read.";
            logger.LogWarning("Sync response rejected for {Source}: {Reason}", source, reason);
            await RecordAsync(profileId, source, startedAt, Failed(reason), cancellationToken);
            return Failure(source, reason);
        }

        var preview = await importService.PreviewAsync(
            parsed, aniListParser.FormatName, profileId, progress, cancellationToken);

        var result = new SyncFetchResult { Source = source, Preview = preview };

        // Recorded now only because there is nothing left to happen. A preview with
        // changes or conflicts in it is a run still waiting on a person, and
        // recording it would let the Sources page report the library as up to date
        // while those changes sit unconfirmed on screen.
        if (result.IsComplete)
        {
            await RecordAsync(
                profileId,
                source,
                startedAt,
                new SyncRun { Outcome = SyncOutcome.NothingToDo, Skipped = preview.UnchangedCount },
                cancellationToken);
        }

        return result;
    }

    public async Task<SyncApplyResult> ApplyAsync(
        ImportPreview preview,
        int profileId,
        AnimeSource source,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        RequireSyncable(source);

        var startedAt = DateTimeOffset.UtcNow;

        var commit = await importService.CommitAsync(preview, profileId, progress, cancellationToken);

        var held = preview.Items.Count(i =>
            i.Action == ImportAction.Conflict && i.Resolution == ConflictResolution.Skip);

        await RecordAsync(
            profileId,
            source,
            startedAt,
            new SyncRun
            {
                Outcome = commit.Created + commit.Updated > 0
                    ? SyncOutcome.Succeeded
                    : SyncOutcome.NothingToDo,
                Created = commit.Created,
                Updated = commit.Updated,
                Skipped = commit.Skipped,
                ConflictsHeld = held,
                SlotsReleased = commit.QueueSlotsReleased
            },
            cancellationToken);

        logger.LogInformation(
            "Sync applied for {Source}: {Created} created, {Updated} updated, {Held} conflicts held",
            source,
            commit.Created,
            commit.Updated,
            held);

        return new SyncApplyResult { Commit = commit, ConflictsHeld = held };
    }

    public async Task<IReadOnlyList<SourceSyncStatus>> GetStatusAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var settings = await context.SourceSyncSettings
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .ToDictionaryAsync(s => s.Source, cancellationToken);

        var account = options.CurrentValue.AniList.UserName;
        var statuses = new List<SourceSyncStatus>(SyncableSources.Length);

        foreach (var source in SyncableSources)
        {
            // Newest run first, and only completed ones exist — nothing writes a row
            // until a run has reached a terminal state.
            //
            // Ordered by key rather than by StartedAt: SQLite cannot order by a
            // DateTimeOffset at all, and this table is only ever appended to, so the
            // key is insertion order and insertion order is chronological.
            var lastRun = await context.SyncRuns
                .AsNoTracking()
                .Where(r => r.ProfileId == profileId && r.Source == source)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            statuses.Add(new SourceSyncStatus
            {
                Source = source,
                Settings = settings.TryGetValue(source, out var stored)
                    ? stored
                    : DefaultSettings(profileId, source),
                IsConfigured = !string.IsNullOrWhiteSpace(account),
                Account = string.IsNullOrWhiteSpace(account) ? null : account,
                LastRun = lastRun
            });
        }

        return statuses;
    }

    /// <summary>
    /// Parses every payload of one fetch, in the user's preferred title language,
    /// and merges them into the single result the preview takes.
    /// </summary>
    private async Task<ParseResult> ParseAsync(
        int profileId,
        AniListFetch fetch,
        CancellationToken cancellationToken)
    {
        var preferredTitle = await LoadPreferredTitleLanguageAsync(profileId, cancellationToken);
        var parts = new List<ParseResult>(fetch.Payloads.Count);

        foreach (var payload in fetch.Payloads)
        {
            using var stream = new MemoryStream(payload, writable: false);
            parts.Add(await aniListParser.ParseAsync(stream, preferredTitle, cancellationToken));
        }

        return ParseResult.Merge(parts);
    }

    private async Task<TitleLanguage> LoadPreferredTitleLanguageAsync(
        int profileId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var preference = await context.ProfileSettings
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .Select(s => (TitleLanguage?)s.PreferredTitleLanguage)
            .FirstOrDefaultAsync(cancellationToken);

        // A profile with no settings row gets romaji, which is what a MyAnimeList
        // library already holds — so the absence of a preference never rewrites
        // titles (D22).
        return preference ?? TitleLanguage.Romaji;
    }

    private async Task<SourceSyncSettings> LoadSettingsAsync(
        int profileId,
        AnimeSource source,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var stored = await context.SourceSyncSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Source == source, cancellationToken);

        // Defaults in memory rather than a row written on first read. A settings row
        // is the user's statement about a source; the sync creating one silently
        // would make "configured" indistinguishable from "looked at once".
        return stored ?? DefaultSettings(profileId, source);
    }

    private static SourceSyncSettings DefaultSettings(int profileId, AnimeSource source) =>
        new() { ProfileId = profileId, Source = source };

    private async Task RecordAsync(
        int profileId,
        AnimeSource source,
        DateTimeOffset startedAt,
        SyncRun run,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        run.ProfileId = profileId;
        run.Source = source;
        run.StartedAt = startedAt;
        run.FinishedAt = DateTimeOffset.UtcNow;

        context.SyncRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static SyncRun Failed(string reason) => new()
    {
        Outcome = SyncOutcome.Failed,

        // Truncated to the column's width rather than left to the database to
        // reject. A failure record that itself fails to save would lose the only
        // evidence of what went wrong.
        FailureReason = reason.Length > 500 ? reason[..500] : reason
    };

    private static SyncFetchResult Failure(AnimeSource source, string reason) =>
        new() { Source = source, FailureReason = reason };

    private static void RequireSyncable(AnimeSource source)
    {
        if (!SyncableSources.Contains(source))
        {
            // A programming error rather than a user-facing failure: nothing offers
            // MyAnimeList as something to sync, because there is no list to fetch.
            throw new ArgumentOutOfRangeException(
                nameof(source), source, "This source cannot be synced.");
        }
    }
}

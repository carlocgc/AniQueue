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
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices(AnimeSource.AniList)] IAnimeListParser aniListParser,
    IImportService importService,
    IOptionsMonitor<SyncOptions> options,
    ILogger<SyncService> logger) : ISyncService
{
    /// <summary>
    /// The sources a sync can actually read. MyAnimeList is absent deliberately —
    /// it is a file import, and nothing here fetches it.
    /// </summary>
    private static readonly AnimeSource[] SyncableSources = [AnimeSource.AniList];

    /// <summary>How many absent titles the status names before it stops listing them.</summary>
    private const int AbsentTitlesShown = 10;

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

        var parsed = await ParseAsync(fetch, cancellationToken);

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

        // Before the preview, because this is the one thing a preview cannot show:
        // it iterates what arrived, so a title that stopped arriving is never
        // considered at all. The response is known to be complete by this point —
        // the client fails rather than truncating, and a merge rejects the whole
        // fetch if any part of it failed — which is the precondition D19 requires.
        var absent = await ReconcileAbsenceAsync(profileId, source, parsed, settings, cancellationToken);

        var preview = await importService.PreviewAsync(
            parsed, aniListParser.FormatName, profileId, progress, cancellationToken);

        var result = new SyncFetchResult { Source = source, Preview = preview, AbsentFlagged = absent };

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
                new SyncRun
                {
                    // Flagging is something that happened, so a run that did it is
                    // not "nothing to change" — that phrasing is how a title
                    // silently leaving the source would go unmentioned.
                    Outcome = absent > 0 ? SyncOutcome.Succeeded : SyncOutcome.NothingToDo,
                    Skipped = preview.UnchangedCount,
                    AbsentFlagged = absent
                },
                cancellationToken);
        }

        return result;
    }

    public async Task<SyncApplyResult> ApplyAsync(
        SyncFetchResult fetch,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        RequireSyncable(fetch.Source);

        if (fetch.Preview is not { } preview)
        {
            throw new ArgumentException("A failed fetch has nothing to apply.", nameof(fetch));
        }

        var source = fetch.Source;
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
                Outcome = commit.Created + commit.Updated + fetch.AbsentFlagged > 0
                    ? SyncOutcome.Succeeded
                    : SyncOutcome.NothingToDo,
                Created = commit.Created,
                Updated = commit.Updated,
                Skipped = commit.Skipped,
                ConflictsHeld = held,
                SlotsReleased = commit.QueueSlotsReleased,
                AbsentFlagged = fetch.AbsentFlagged
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

    public async Task<UnattendedSyncResult> RunUnattendedAsync(
        int profileId,
        AnimeSource source,
        CancellationToken cancellationToken = default)
    {
        RequireSyncable(source);

        var fetch = await FetchAsync(profileId, source, progress: null, cancellationToken);

        // Both already recorded by the fetch, and both are terminal: a failure, or a
        // list that matched. Nothing here has a decision to make.
        if (!fetch.Succeeded)
        {
            return new UnattendedSyncResult
            {
                Source = source,
                Outcome = SyncOutcome.Failed,
                FailureReason = fetch.FailureReason
            };
        }

        var preview = fetch.Preview!;

        if (fetch.IsComplete)
        {
            return new UnattendedSyncResult
            {
                Source = source,
                Outcome = fetch.AbsentFlagged > 0 ? SyncOutcome.Succeeded : SyncOutcome.NothingToDo,
                AbsentFlagged = fetch.AbsentFlagged
            };
        }

        var settings = await LoadSettingsAsync(profileId, source, cancellationToken);

        if (settings.ConflictPolicy == SyncConflictPolicy.LinkToExisting)
        {
            ResolveConflictsByExactTitle(preview);
        }

        if (settings.ApplyUnattended && preview.HasApplicableChanges)
        {
            var applied = await ApplyAsync(fetch, profileId, progress: null, cancellationToken);

            return new UnattendedSyncResult
            {
                Source = source,
                Outcome = SyncOutcome.Succeeded,
                Created = applied.Commit.Created,
                Updated = applied.Commit.Updated,
                SlotsReleased = applied.Commit.QueueSlotsReleased,
                AbsentFlagged = fetch.AbsentFlagged,
                ConflictsHeld = applied.ConflictsHeld
            };
        }

        // Nothing was applied — either because this source asks first, or because
        // everything left is a conflict. Recorded rather than passed over: a run
        // that found twelve changes and applied none of them is not a sync that
        // found nothing, and the Sources page has to be able to tell the difference
        // (§4). Only the count is kept, per D21 — a held preview is stale within the
        // hour, and the user's visit re-fetches and recomputes it.
        var changesHeld = preview.CreateCount + preview.UpdateCount + preview.ResolvedConflictCount;
        var conflictsHeld = preview.Items.Count(i =>
            i.Action == ImportAction.Conflict && i.Resolution == ConflictResolution.Skip);

        await RecordAsync(
            profileId,
            source,
            DateTimeOffset.UtcNow,
            new SyncRun
            {
                Outcome = SyncOutcome.HeldForReview,
                Skipped = preview.UnchangedCount,
                ChangesHeld = changesHeld,
                ConflictsHeld = conflictsHeld,
                AbsentFlagged = fetch.AbsentFlagged
            },
            cancellationToken);

        logger.LogInformation(
            "Unattended sync for {Source} held {Changes} changes and {Conflicts} conflicts for review",
            source,
            changesHeld,
            conflictsHeld);

        return new UnattendedSyncResult
        {
            Source = source,
            Outcome = SyncOutcome.HeldForReview,
            ChangesHeld = changesHeld,
            ConflictsHeld = conflictsHeld,
            AbsentFlagged = fetch.AbsentFlagged
        };
    }

    /// <summary>
    /// Opts a conflict in to linking, where the titles are letter-for-letter the
    /// same (D21).
    /// </summary>
    /// <remarks>
    /// The only resolution that may be automated, because it is the only one that
    /// converges: writing the identifier is what stops the entry conflicting again
    /// on every subsequent run. Skipping looks safer and does not converge — the row
    /// stays unidentified and the pending count never clears.
    ///
    /// Two guards, and they are what make silent title-based merging defensible at
    /// all. There must be exactly one candidate — an ambiguous multi-match carries
    /// no <see cref="ImportPreviewItem.ExistingAnimeId"/> at all, so it cannot be
    /// picked up here — and the titles must match exactly, ignoring case only. This
    /// is not the similarity heuristic D10 rejected.
    ///
    /// <c>ImportAsNew</c> is never applied without a person. It duplicates the row,
    /// both copies appear in the backlog, both are queueable, and nothing in the MVP
    /// can delete either.
    /// </remarks>
    private static void ResolveConflictsByExactTitle(ImportPreview preview)
    {
        foreach (var item in preview.Items)
        {
            if (item is { Action: ImportAction.Conflict, ExistingAnimeId: not null, ExistingTitle: { } existing }
                && string.Equals(existing, item.Entry.Title, StringComparison.OrdinalIgnoreCase))
            {
                item.Resolution = ConflictResolution.LinkToExisting;
            }
        }
    }

    /// <summary>
    /// Marks the titles this source used to list and no longer does, and unmarks the
    /// ones it has started listing again (D19).
    /// </summary>
    /// <remarks>
    /// <b>Scope is structural, not configured.</b> Only rows carrying this source's
    /// identifier are ever considered, so a MyAnimeList-only title — or one added by
    /// hand — cannot be reached by an AniList policy whatever the user has set. That
    /// is what protects somebody consolidating two separately-maintained lists.
    ///
    /// <b>An empty fetch marks nothing.</b> A truncated response, a paging bug, a
    /// mistyped account and "the user deleted everything" are indistinguishable from
    /// here, and D19 is explicit that acting on that reading is the one failure with
    /// no recovery path. The client fails rather than truncating and a merge rejects
    /// a fetch any part of which failed, so reaching this method already means the
    /// response was structurally complete; the zero-entry guard covers the remaining
    /// case of a complete response that is simply empty.
    ///
    /// Nothing is removed. <see cref="SyncAbsencePolicy.Remove"/> waits for the phase
    /// that supplies backup and restore, so the mark is currently the whole of the
    /// behaviour — which is also why it is safe to write during a fetch that has not
    /// been confirmed: it records what the source said, and the next fetch that says
    /// otherwise clears it.
    /// </remarks>
    private async Task<int> ReconcileAbsenceAsync(
        int profileId,
        AnimeSource source,
        ParseResult parsed,
        SourceSyncSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.AbsencePolicy == SyncAbsencePolicy.Ignore || parsed.Entries.Count == 0)
        {
            return 0;
        }

        var listed = parsed.Entries
            .SelectMany(e => e.ExternalIds)
            .Where(id => id.Source == source)
            .Select(id => id.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Nothing in the response carried an identifier for the source being read,
        // which is not a list of absences — it is a response this code does not
        // understand well enough to draw conclusions from.
        if (listed.Count == 0)
        {
            return 0;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Restricted to titles this profile actually has an entry for. An identifier
        // row survives on a catalogue row kept for franchise grouping or
        // recommendation history, and flagging one of those would count a title the
        // user does not have as one they have lost.
        var rows = await context.AnimeExternalIds
            .Where(x => x.Source == source
                && context.LibraryEntries.Any(e => e.ProfileId == profileId && e.AnimeId == x.AnimeId))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var flagged = 0;
        var cleared = 0;

        foreach (var row in rows)
        {
            if (listed.Contains(row.ExternalId))
            {
                // Listed again — or never gone, which is the usual case and writes
                // nothing because the value is already null.
                if (row.MissingFromSourceAt is not null)
                {
                    row.MissingFromSourceAt = null;
                    cleared++;
                }
            }
            else if (row.MissingFromSourceAt is null)
            {
                row.MissingFromSourceAt = now;
                flagged++;
            }
        }

        if (flagged + cleared == 0)
        {
            // The steady state, and the reason for counting rather than saving
            // unconditionally: an idle poll must not open a write transaction at all,
            // because it contends with the user for SQLite's single writer (§9).
            return 0;
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Source} absence: {Flagged} titles no longer listed, {Cleared} listed again",
            source,
            flagged,
            cleared);

        return flagged;
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
            var runs = context.SyncRuns
                .AsNoTracking()
                .Where(r => r.ProfileId == profileId && r.Source == source);

            var lastRun = await runs
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            // "Reached the source" rather than "changed something": a run that found
            // nothing to do, or held its changes for review, still proves the account
            // is readable and the list is current.
            var lastSuccess = await runs
                .Where(r => r.Outcome != SyncOutcome.Failed)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var lastFailure = await runs
                .Where(r => r.Outcome == SyncOutcome.Failed)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            // Everything after the last success is by definition a failure, so this
            // is a count rather than a scan. Zero when the newest run succeeded.
            var lastSuccessId = lastSuccess?.Id ?? 0;
            var consecutiveFailures = lastFailure is null
                ? 0
                : await runs.CountAsync(r => r.Id > lastSuccessId, cancellationToken);

            var absentQuery = context.AnimeExternalIds
                .AsNoTracking()
                .Where(x => x.Source == source
                    && x.MissingFromSourceAt != null
                    && context.LibraryEntries.Any(e => e.ProfileId == profileId && e.AnimeId == x.AnimeId));

            var absentCount = await absentQuery.CountAsync(cancellationToken);

            // Named only when there are any, and only a few of them: the page is
            // reminding the user to go and look, not reporting.
            var absentTitles = absentCount == 0
                ? []
                : await absentQuery
                    .Select(x => x.Anime!.Title)
                    .OrderBy(title => title)
                    .Take(AbsentTitlesShown)
                    .ToListAsync(cancellationToken);

            statuses.Add(new SourceSyncStatus
            {
                Source = source,
                Settings = settings.TryGetValue(source, out var stored)
                    ? stored
                    : DefaultSettings(profileId, source),
                IsConfigured = !string.IsNullOrWhiteSpace(account),
                Account = string.IsNullOrWhiteSpace(account) ? null : account,
                LastRun = lastRun,
                LastSuccess = lastSuccess,
                LastFailure = lastFailure,
                ConsecutiveFailures = consecutiveFailures,
                AbsentCount = absentCount,
                AbsentTitles = absentTitles
            });
        }

        return statuses;
    }

    public async Task SaveSettingsAsync(
        SourceSyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RequireSyncable(settings.Source);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var stored = await context.SourceSyncSettings.FirstOrDefaultAsync(
            s => s.ProfileId == settings.ProfileId && s.Source == settings.Source, cancellationToken);

        if (stored is null)
        {
            context.SourceSyncSettings.Add(settings);
        }
        else
        {
            // Copied field by field rather than attached, because the instance the
            // page edited came back through a page render and carries no identity
            // this context would recognise.
            stored.IsEnabled = settings.IsEnabled;
            stored.PrecedenceRank = settings.PrecedenceRank;
            stored.ApplyUnattended = settings.ApplyUnattended;
            stored.ConflictPolicy = settings.ConflictPolicy;
            stored.AbsencePolicy = settings.AbsencePolicy;
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Sync settings saved for {Source}", settings.Source);
    }

    public async Task SavePreferredTitleLanguageAsync(
        int profileId,
        TitleLanguage language,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var settings = await context.ProfileSettings
            .FirstOrDefaultAsync(s => s.ProfileId == profileId, cancellationToken);

        if (settings is null)
        {
            // Nothing has created a settings row for this profile yet. The rest of
            // the defaults come from the entity, which is where they are documented.
            settings = new ProfileSettings { ProfileId = profileId, DisplayName = "AniQueue" };
            context.ProfileSettings.Add(settings);
        }

        settings.PreferredTitleLanguage = language;
        await context.SaveChangesAsync(cancellationToken);

        await RewriteDisplayTitlesAsync(context, language, cancellationToken);

        logger.LogInformation("Title language set to {Language}", language);
    }

    /// <summary>
    /// Recomputes every stored display title from the variants beside it.
    /// </summary>
    /// <remarks>
    /// This is what makes the preference a preference. It used to take effect only
    /// when the next sync happened to rewrite the row, which meant a library already
    /// up to date could not change language at all without re-fetching the whole list
    /// — a display choice wearing a sync's clothes (D22).
    ///
    /// One statement rather than a load-modify-save loop: this touches every row in
    /// the catalogue, and a few thousand tracked entities to write one column each is
    /// a poor trade for a setting somebody flips while looking at the page.
    ///
    /// Rows with no variants — every manual entry, everything from a MyAnimeList
    /// export — keep the only title they have, because the coalesce falls through to
    /// it. That is why the Sources page can say those titles are unaffected.
    /// </remarks>
    private static async Task RewriteDisplayTitlesAsync(
        AniQueueDbContext context,
        TitleLanguage language,
        CancellationToken cancellationToken)
    {
        // The chain matches TitleSelection.Resolve, and the pair are tested together
        // so the two cannot drift into showing different languages on different pages.
        await (language switch
        {
            TitleLanguage.English => context.Anime.ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Title, a => a.TitleEnglish ?? a.TitleRomaji ?? a.TitleNative ?? a.Title),
                cancellationToken),

            TitleLanguage.Native => context.Anime.ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Title, a => a.TitleNative ?? a.TitleRomaji ?? a.TitleEnglish ?? a.Title),
                cancellationToken),

            _ => context.Anime.ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Title, a => a.TitleRomaji ?? a.TitleEnglish ?? a.TitleNative ?? a.Title),
                cancellationToken)
        });
    }

    public Task<TitleLanguage> GetPreferredTitleLanguageAsync(
        int profileId,
        CancellationToken cancellationToken = default) =>
        LoadPreferredTitleLanguageAsync(profileId, cancellationToken);

    /// <summary>
    /// Parses every payload of one fetch and merges them into the single result the
    /// preview takes.
    /// </summary>
    /// <remarks>
    /// No title preference passes through here. The parser carries every variant the
    /// source published and the import resolves which to display, so one parse serves
    /// any preference and changing it later needs no fetch at all (D22).
    /// </remarks>
    private async Task<ParseResult> ParseAsync(AniListFetch fetch, CancellationToken cancellationToken)
    {
        var parts = new List<ParseResult>(fetch.Payloads.Count);

        foreach (var payload in fetch.Payloads)
        {
            using var stream = new MemoryStream(payload, writable: false);
            parts.Add(await aniListParser.ParseAsync(stream, cancellationToken));
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

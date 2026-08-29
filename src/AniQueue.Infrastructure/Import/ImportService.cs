using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Progress;
using AniQueue.Core.Queue;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Import;

/// <summary>
/// Matches parsed entries against the library and applies them.
///
/// Two things this type is careful about:
///
/// 1. <see cref="PreviewAsync"/> never writes. The user has to see the consequence
///    and confirm before anything changes.
/// 2. An import brings catalogue data and watch progress. It never touches what
///    the user curated here — notes, queue position, recommendation
///    data. Re-importing an export must not undo an evening
///    spent organising the backlog.
///
/// The one thing an import does change about the queue is which slots are still
/// needed, and it does that by asking the queue rather than by editing it — see
/// the advancement step at the end of <see cref="CommitAsync"/>.
/// </summary>
public sealed class ImportService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IQueueService queueService,
    IOptionsMonitor<SyncOptions> syncOptions,
    ILogger<ImportService> logger) : IImportService
{
    public async Task<ImportPreview> PreviewAsync(
        Stream input,
        IAnimeListParser parser,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parser);

        progress?.Report(new OperationProgress($"Reading the {parser.FormatName} file"));

        var parsed = await parser.ParseAsync(input, cancellationToken);

        return await PreviewAsync(parsed, parser.FormatName, profileId, progress, cancellationToken);
    }

    public async Task<ImportPreview> PreviewAsync(
        ParseResult parsed,
        string formatName,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        logger.LogInformation("Import preview started using {Format}", formatName);

        if (parsed.IsFileRejected)
        {
            logger.LogWarning("Import payload rejected by {Format} parser", formatName);
            return ImportPreview.Rejected(formatName, parsed.Problems);
        }

        progress?.Report(new OperationProgress(
            $"Read {parsed.Entries.Count} {(parsed.Entries.Count == 1 ? "entry" : "entries")}"));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Which title variant this profile reads, so the preview compares — and the
        // commit writes — the name the user will actually see.
        var preferredTitle = await LoadPreferredTitleAsync(context, profileId, cancellationToken);

        progress?.Report(new OperationProgress("Comparing against your library"));
        var library = await LoadMatchCandidatesAsync(context, profileId, cancellationToken);

        var items = new List<ImportPreviewItem>(parsed.Entries.Count);
        var matched = 0;

        // Identifiers this file has already used, and the title that used them.
        // A file claiming one identifier twice would violate the uniqueness index
        // at commit and abort the whole import, so it is caught here instead and
        // reported against the entry that caused it.
        var claimed = new Dictionary<ExternalIdentifier, string>();

        foreach (var entry in parsed.Entries)
        {
            items.Add(BuildPreviewItem(entry, library, claimed, preferredTitle));

            foreach (var identifier in entry.ExternalIds)
            {
                claimed.TryAdd(identifier, entry.Title);
            }

            matched++;

            progress?.Report(new OperationProgress(
                "Comparing against your library", matched, parsed.Entries.Count));
        }

        progress?.Report(new OperationProgress("Preparing the preview"));

        var preview = new ImportPreview
        {
            FormatName = formatName,
            Items = items,
            Problems = parsed.Problems
        };

        logger.LogInformation(
            "Import preview generated: {Create} new, {Update} updated, {Unchanged} unchanged, "
            + "{Conflict} conflicts, {Invalid} invalid",
            preview.CreateCount,
            preview.UpdateCount,
            preview.UnchangedCount,
            preview.ConflictCount,
            preview.InvalidCount);

        return preview;
    }

    public async Task<ImportCommitResult> CommitAsync(
        ImportPreview preview,
        int profileId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (preview.IsFileRejected)
        {
            throw new InvalidOperationException("A rejected import cannot be committed.");
        }

        progress?.Report(new OperationProgress("Opening a transaction"));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var processed = 0;
        var now = DateTimeOffset.UtcNow;

        // Identifiers written during this commit but not yet flushed. Pending adds
        // are invisible to a query, so without this a second entry claiming the same
        // identifier would be added again and the unique index would abort the whole
        // import at the final save.
        var written = new HashSet<ExternalIdentifier>();

        // Who outranks whom, for titles two sources both describe. The seat is always
        // occupied, so this always has an answer — a single-tracker setup is
        // unaffected either way, because precedence only fires where two of them
        // describe one title. A file naming nobody is read as a file naming the
        // default, which is where that default lives.
        //
        // Every source gets an entry, not just the primary. MayWriteTracking
        // short-circuits when either source is missing a rank, on the grounds that an
        // unranked source is not outranked by anything, so a map holding only the
        // winner would make every contest permissive.
        //
        // Manual is left out, as it always was. A hand-added row is the user's own
        // work, and ranking it would let a sync outrank them.
        var primary = syncOptions.CurrentValue.PrimarySource
            ?? UserSettings.Defaults.SyncPrimarySource;

        var precedence = Enum.GetValues<AnimeSource>()
            .Where(source => source != AnimeSource.Manual)
            .ToDictionary(source => source, source => source == primary ? PrimaryRank : DemotedRank);

        var preferredTitle = await LoadPreferredTitleAsync(context, profileId, cancellationToken);

        // Both vocabularies, once, for the whole commit.
        var taxonomy = await TaxonomyCache.LoadAsync(context, cancellationToken);

        foreach (var item in preview.Items)
        {
            processed++;
            progress?.Report(new OperationProgress(
                "Saving your library", processed, preview.Items.Count));

            if (item.Action == ImportAction.Unchanged)
            {
                skipped++;
                continue;
            }

            if (item.Action == ImportAction.Conflict)
            {
                if (item.Resolution == ConflictResolution.Skip)
                {
                    skipped++;
                    continue;
                }

                if (item.Resolution == ConflictResolution.LinkToExisting)
                {
                    var linked = await LinkToExistingAsync(
                        context, taxonomy, item, now, preferredTitle, precedence, cancellationToken);
                    if (linked is null)
                    {
                        // The record the user chose has since gone. Skipping is safer
                        // than silently creating something they did not ask for.
                        skipped++;
                        continue;
                    }

                    await EnsureIdentifiersAsync(context, linked, item.Entry, written, cancellationToken);
                    await UpsertLibraryEntryAsync(context, profileId, linked.Id, item.Entry, now, precedence, cancellationToken);
                    updated++;
                    continue;
                }

                // ImportAsNew falls through to the ordinary create path below.
            }

            // Re-resolve rather than trusting the id captured during preview. The
            // preview is a snapshot, and re-resolving is what makes committing the
            // same preview twice a no-op instead of a unique-index violation.
            var anime = await FindExistingAsync(context, item.Entry, cancellationToken);

            if (anime is null)
            {
                anime = CreateAnime(item.Entry, now, preferredTitle);
                context.Anime.Add(anime);

                // Before the save rather than after, so the title and its genre and
                // studio links insert together. A new title has nothing stored to
                // preserve, which is why it may always overwrite.
                ApplyTaxonomy(context, taxonomy, anime, item.Entry, mayOverwrite: true);

                await context.SaveChangesAsync(cancellationToken);
                created++;
            }
            else
            {
                var mayOverwrite = OutranksOtherSources(anime, item.Entry, precedence);

                ApplyCatalogueFields(anime, item.Entry, now, preferredTitle, mayOverwrite);
                ApplyTaxonomy(context, taxonomy, anime, item.Entry, mayOverwrite);
                updated++;
            }

            await EnsureIdentifiersAsync(context, anime, item.Entry, written, cancellationToken);
            await UpsertLibraryEntryAsync(context, profileId, anime.Id, item.Entry, now, precedence, cancellationToken);
        }

        progress?.Report(new OperationProgress("Committing the transaction"));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Deliberately after the commit, in its own transaction rather than inside
        // this one.
        //
        // The import is the user's data and must land whatever happens next;
        // advancement is a derived tidy-up that is idempotent and recomputed from
        // scratch every time. Folding it into the transaction above would let a
        // failure to tidy the queue roll back an import the user had confirmed, to
        // fix something the next import would fix anyway.
        progress?.Report(new OperationProgress("Bringing Up Next up to date"));

        var released = await queueService.AdvanceAsync(profileId, cancellationToken);

        logger.LogInformation(
            "Import committed: {Created} created, {Updated} updated, {Skipped} skipped, "
            + "{Released} queue slots released",
            created,
            updated,
            skipped,
            released);

        return new ImportCommitResult
        {
            Created = created,
            Updated = updated,
            Skipped = skipped,
            QueueSlotsReleased = released
        };
    }


    /// <summary>
    /// The title to display for this entry, under this profile's preference.
    /// </summary>
    /// <remarks>
    /// Resolved where the row is written rather than by the parser, so one parse
    /// serves every preference and changing the preference later needs no re-fetch —
    /// only a recompute from the variants stored alongside.
    /// </remarks>
    private static string DisplayTitle(ParsedLibraryEntry entry, TitleLanguage preferred) =>
        TitleSelection.Resolve(
            preferred, entry.TitleRomaji, entry.TitleEnglish, entry.TitleNative, entry.Title);

    /// <summary>
    /// Whether this entry knows a title variant the stored row does not.
    /// </summary>
    /// <summary>
    /// Whether an incoming set says something different from what is stored.
    /// </summary>
    /// <remarks>
    /// <b>Empty incoming is silence and never differs</b>, which is the collection
    /// form of the rule <c>Merge</c> keeps for scalars: a source that does not carry
    /// something has not said it is absent. A MyAnimeList export publishes no
    /// genres at all, so without this a re-import would report — and then apply —
    /// the removal of every genre AniList supplied.
    ///
    /// Order-insensitive and case-insensitive, because neither is a fact about the
    /// title: AniList is free to return the same genres in a different order, and
    /// reporting that as a change would make an idle sync look busy.
    /// </remarks>
    private static bool Differs(IReadOnlyList<string> incoming, IReadOnlyList<string> stored)
    {
        if (incoming.Count == 0)
        {
            return false;
        }

        return !incoming.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(stored);
    }

    private static bool StoresNewVariants(ParsedLibraryEntry entry, AnimeSnapshot existing) =>
        IsNew(entry.TitleRomaji, existing.TitleRomaji) ||
        IsNew(entry.TitleEnglish, existing.TitleEnglish) ||
        IsNew(entry.TitleNative, existing.TitleNative);

    private static bool IsNew(string? incoming, string? stored) =>
        incoming is not null && !string.Equals(incoming, stored, StringComparison.Ordinal);

    /// <summary>
    /// Which title variant this profile reads. Romaji for a profile with no settings
    /// row, which is what a MyAnimeList library already holds — so the absence of a
    /// preference never rewrites a title.
    /// </summary>
    private static async Task<TitleLanguage> LoadPreferredTitleAsync(
        AniQueueDbContext context,
        int profileId,
        CancellationToken cancellationToken) =>
        await context.ProfileSettings
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .Select(s => (TitleLanguage?)s.PreferredTitleLanguage)
            .FirstOrDefaultAsync(cancellationToken) ?? TitleLanguage.Romaji;

    /// <summary>
    /// Loads a projection of every title plus this profile's entries.
    ///
    /// Deliberately loads all rows rather than filtering by the incoming
    /// identifiers: a large export would otherwise produce an IN clause with
    /// thousands of parameters, and SQLite has a hard parameter ceiling. A few
    /// thousand narrow rows is a cheaper and far more predictable trade, and
    /// import is a bulk operation the user has explicitly asked for.
    /// </summary>
    private static async Task<MatchCandidates> LoadMatchCandidatesAsync(
        AniQueueDbContext context,
        int profileId,
        CancellationToken cancellationToken)
    {
        var anime = await context.Anime
            .AsNoTracking()
            .Select(a => new AnimeSnapshot(
                a.Id,
                a.Source,
                a.Title,
                a.TitleRomaji,
                a.TitleEnglish,
                a.TitleNative,
                a.MediaType,
                a.EpisodeCount,
                a.EpisodeDurationMinutes,
                a.ReleaseYear,
                a.Images.Any(i => i.Rendition == ImageRendition.Thumbnail),
                a.Images.Any(i => i.Rendition == ImageRendition.Full),
                a.Description,
                a.Genres.Select(g => g.Genre!.Name).ToList(),
                a.Studios.Select(s => s.Studio!.Name).ToList(),
                a.Studios.Where(s => s.IsMain).Select(s => s.Studio!.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        // Loaded whole for the same reason the catalogue is: an IN clause built
        // from a large export would exceed SQLite's parameter ceiling, and this
        // table is narrower than the one above.
        var identifiers = await context.AnimeExternalIds
            .AsNoTracking()
            .Select(x => new { x.Source, x.ExternalId, x.AnimeId })
            .ToListAsync(cancellationToken);

        var byIdentifier = identifiers.ToDictionary(
            x => new ExternalIdentifier(x.Source, x.ExternalId),
            x => x.AnimeId);

        // Which titles carry any identifier at all. This is what distinguishes a
        // hand-added row from an imported one now that there is no null column to
        // test, and the manual-twin check below depends on it.
        var identified = identifiers.Select(x => x.AnimeId).ToHashSet();

        var entries = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId)
            .Select(e => new EntrySnapshot(
                e.AnimeId, e.Status, e.EpisodesWatched, e.UserScore, e.DateStarted, e.DateCompleted))
            .ToDictionaryAsync(e => e.AnimeId, cancellationToken);

        return new MatchCandidates(anime, byIdentifier, identified, entries);
    }

    private static ImportPreviewItem BuildPreviewItem(
        ParsedLibraryEntry entry,
        MatchCandidates library,
        Dictionary<ExternalIdentifier, string> claimed,
        TitleLanguage preferredTitle)
    {
        if (entry.ExternalIds.Count > 0)
        {
            // An identifier this same file already used. Two entries cannot be the
            // same title on the same service, so one of them is wrong, and applying
            // either silently would be a guess.
            foreach (var identifier in entry.ExternalIds)
            {
                if (claimed.TryGetValue(identifier, out var firstClaimant))
                {
                    return new ImportPreviewItem
                    {
                        Entry = entry,
                        Action = ImportAction.Conflict,
                        ExistingTitle = firstClaimant,
                        ConflictReason =
                            $"This file already used {identifier.Source} id {identifier.Value} "
                            + $"for '{firstClaimant}'. Two entries cannot share one identifier."
                    };
                }
            }

            // Every identifier is tried, not just the one matching this parser's own
            // service, so an AniList entry carrying a MyAnimeList id matches a
            // MyAnimeList-imported row instead of duplicating it, and the same holds
            // in the other direction.
            var resolved = entry.ExternalIds
                .Where(library.ByIdentifier.ContainsKey)
                .Select(id => library.ByIdentifier[id])
                .Distinct()
                .ToList();

            // Identifiers that disagree about which local title they mean. Almost
            // always two local rows that are really one title, and merging them is
            // not something this pipeline can do — so the user decides rather than
            // the first identifier winning by position.
            if (resolved.Count > 1)
            {
                var names = resolved
                    .Select(id => library.Anime.FirstOrDefault(a => a.Id == id)?.Title)
                    .OfType<string>();

                return new ImportPreviewItem
                {
                    Entry = entry,
                    Action = ImportAction.Conflict,
                    ConflictReason =
                        $"Its identifiers point at {resolved.Count} different titles already in "
                        + $"your library ({string.Join(", ", names)}), which cannot all be this one."
                };
            }

            if (resolved.Count == 1)
            {
                var existing = library.Anime.First(a => a.Id == resolved[0]);
                return CompareWithExisting(entry, existing, library, preferredTitle);
            }

            // No identifier match, but a same-titled entry with no identifier of its
            // own is very likely the title the user added by hand before importing.
            // Creating a second copy would be a silent duplicate, so it is surfaced.
            var manualTwins = library.Anime
                .Where(a => !library.Identified.Contains(a.Id)
                    && string.Equals(a.Title, entry.Title, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // More than one, and there is no candidate to name: picking the first
            // would be answering a question nobody asked, and the row it picked would
            // be decided by query order. Reported without an id, which is also what
            // stops an unattended run from linking it — a resolution needs a
            // candidate, and this deliberately has none.
            if (manualTwins.Count > 1)
            {
                return new ImportPreviewItem
                {
                    Entry = entry,
                    Action = ImportAction.Conflict,
                    ConflictReason =
                        $"{manualTwins.Count} entries with this title already exist without a "
                        + "source identifier, so there is no way to tell which one this is."
                };
            }

            if (manualTwins.Count == 1)
            {
                return new ImportPreviewItem
                {
                    Entry = entry,
                    Action = ImportAction.Conflict,
                    ExistingAnimeId = manualTwins[0].Id,
                    ExistingTitle = manualTwins[0].Title,
                    ConflictReason =
                        "An entry with this title already exists without a source identifier. "
                        + "Importing would create a duplicate."
                };
            }

            return new ImportPreviewItem { Entry = entry, Action = ImportAction.Create };
        }

        // No identifier to match on, so fall back to the title — cautiously.
        var titleMatches = library.Anime
            .Where(a => string.Equals(a.Title, entry.Title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return titleMatches.Count switch
        {
            0 => new ImportPreviewItem { Entry = entry, Action = ImportAction.Create },

            // A single title match is still not proof of identity, and merging the
            // wrong two titles is much harder to undo than confirming one row.
            1 => new ImportPreviewItem
            {
                Entry = entry,
                Action = ImportAction.Conflict,
                ExistingAnimeId = titleMatches[0].Id,
                ExistingTitle = titleMatches[0].Title,
                ConflictReason = "Matched by title only, with no source identifier to confirm it."
            },

            _ => new ImportPreviewItem
            {
                Entry = entry,
                Action = ImportAction.Conflict,
                ConflictReason = $"{titleMatches.Count} existing titles match this name."
            }
        };
    }

    private static ImportPreviewItem CompareWithExisting(
        ParsedLibraryEntry entry,
        AnimeSnapshot existing,
        MatchCandidates library,
        TitleLanguage preferredTitle)
    {
        var changes = new List<string>();
        var title = DisplayTitle(entry, preferredTitle);

        if (!string.Equals(existing.Title, title, StringComparison.Ordinal))
        {
            changes.Add($"Title: '{existing.Title}' → '{title}'");
        }

        if (existing.MediaType != entry.MediaType && entry.MediaType != MediaType.Unknown)
        {
            changes.Add($"Type: {existing.MediaType} → {entry.MediaType}");
        }

        // Reported so the row becomes an update, which is what actually stores them.
        // Without this a library already synced under the old single-alternative
        // column would resolve to the same displayed title, count as unchanged, and
        // never record which language its titles are — leaving the preference unable
        // to switch anything.
        if (StoresNewVariants(entry, existing))
        {
            changes.Add("Records its title in each language");
        }

        // Only an actual value replaces a known one; an import that has forgotten
        // the episode count must not erase one already recorded.
        if (entry.EpisodeCount is not null && existing.EpisodeCount != entry.EpisodeCount)
        {
            changes.Add($"Episodes: {Display(existing.EpisodeCount, "unknown")} → {entry.EpisodeCount}");
        }

        if (entry.EpisodeDurationMinutes is not null &&
            existing.EpisodeDurationMinutes != entry.EpisodeDurationMinutes)
        {
            changes.Add(
                $"Episode length: {Display(existing.EpisodeDurationMinutes, "unknown")} → "
                + $"{entry.EpisodeDurationMinutes} min");
        }

        if (entry.ReleaseYear is not null && existing.ReleaseYear != entry.ReleaseYear)
        {
            changes.Add($"Year: {Display(existing.ReleaseYear, "unknown")} → {entry.ReleaseYear}");
        }

        // Deliberately only reported when there is currently no art at all.
        //
        // A cover URL that merely changed is almost always the same picture behind
        // a rotated CDN path, and reporting it would turn an otherwise idle sync
        // into a library-wide list of "updated" rows for the user to review. Gaining
        // art where there was none is a real change and is shown.
        if (entry.CoverImageUrl is not null && !existing.HasThumbnail)
        {
            changes.Add("Adds cover art");
        }

        // Reported separately because it is gained separately: a title that already
        // has a thumbnail would otherwise look unchanged, and an unchanged item is
        // skipped outright at commit, so its full-size cover would never be written.
        if (entry.CoverImageFullUrl is not null && !existing.HasFullCover)
        {
            changes.Add("Adds a full-size cover");
        }

        // Unlike the cover URL, a differing synopsis is never spurious, so it is
        // reported whenever it differs rather than only when it is gained.
        if (entry.Description is { Length: > 0 }
            && !string.Equals(entry.Description, existing.Description, StringComparison.Ordinal))
        {
            changes.Add(existing.Description is { Length: > 0 } ? "Updates the synopsis" : "Adds a synopsis");
        }

        // An empty incoming set is silence and is never a change — the same rule the
        // merge itself keeps, and the reason a MyAnimeList re-import does not report
        // every AniList-sourced title as losing its genres.
        if (Differs(entry.Genres, existing.Genres))
        {
            changes.Add(existing.Genres.Count == 0 ? "Adds genres" : "Updates genres");
        }

        // Two questions, because the answer to one does not imply the other: which
        // companies are credited, and which of them is the studio. A title recredited
        // from Wit Studio to MAPPA credits both before and after.
        if (entry.Studios.Count > 0)
        {
            var incomingMain = entry.Studios.FirstOrDefault(s => s.IsMain).Name;

            if (Differs(entry.Studios.Select(s => s.Name).ToList(), existing.Studios)
                || !string.Equals(incomingMain, existing.MainStudio, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(existing.Studios.Count == 0 ? "Adds studios" : "Updates studios");
            }
        }

        // Identifiers this record does not carry yet. Shown because it is a real
        // change, and it is what lets later syncs match cleanly rather than conflict.
        // Re-importing the same file adds nothing, so idempotency is unaffected.
        foreach (var identifier in entry.ExternalIds.Where(id => !library.ByIdentifier.ContainsKey(id)))
        {
            changes.Add($"Links to {identifier.Source} id {identifier.Value}");
        }

        if (library.Entries.TryGetValue(existing.Id, out var current))
        {
            if (current.Status != entry.Status)
            {
                changes.Add($"Status: {current.Status} → {entry.Status}");
            }

            if (current.EpisodesWatched != entry.EpisodesWatched)
            {
                changes.Add($"Watched: {current.EpisodesWatched} → {entry.EpisodesWatched}");
            }

            if (current.UserScore != entry.UserScore)
            {
                changes.Add($"Score: {Display(current.UserScore, "none")} → {Display(entry.UserScore, "none")}");
            }

            if (entry.DateStarted is not null && current.DateStarted != entry.DateStarted)
            {
                changes.Add($"Started: {entry.DateStarted}");
            }

            if (entry.DateCompleted is not null && current.DateCompleted != entry.DateCompleted)
            {
                changes.Add($"Finished: {entry.DateCompleted}");
            }
        }
        else
        {
            changes.Add("Adds this title to your library");
        }

        return new ImportPreviewItem
        {
            Entry = entry,
            Action = changes.Count == 0 ? ImportAction.Unchanged : ImportAction.Update,
            ExistingAnimeId = existing.Id,
            Changes = changes
        };
    }

    /// <summary>
    /// Adopts the incoming metadata onto the record the user identified as the same
    /// title, and returns it so its identifiers can be written.
    ///
    /// Attaching the identifier is the substance of the operation: it is what stops
    /// the entry conflicting again on every subsequent import. Everything else on
    /// the existing record is left alone, as with any other import.
    /// </summary>
    /// <remarks>
    /// <see cref="Anime.Source"/> is deliberately not reassigned. It records how the
    /// record came to exist, and a hand-added title that has since been linked was
    /// still hand-added. What changes is which services identify it, and that
    /// is now a separate table — so the backlog's source filter, which asks "is this
    /// on MyAnimeList", finds it either way.
    /// </remarks>
    private static async Task<Anime?> LinkToExistingAsync(
        AniQueueDbContext context,
        TaxonomyCache taxonomy,
        ImportPreviewItem item,
        DateTimeOffset now,
        TitleLanguage preferredTitle,
        IReadOnlyDictionary<AnimeSource, int> precedence,
        CancellationToken cancellationToken)
    {
        if (item.ExistingAnimeId is not { } existingId)
        {
            return null;
        }

        // Identifiers included because precedence is decided per title, from which
        // sources describe this row.
        var existing = await context.Anime
            .Include(a => a.ExternalIds)
            .Include(a => a.Images)
            .Include(a => a.Genres)
            .Include(a => a.Studios)

            // Four collections on one row multiply together in a single query — a
            // title with two identifiers, two renditions, four genres and five
            // studios comes back as eighty rows to build one entity from. Split, as
            // EF itself warns to.
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == existingId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var mayOverwrite = OutranksOtherSources(existing, item.Entry, precedence);

        ApplyCatalogueFields(existing, item.Entry, now, preferredTitle, mayOverwrite);
        ApplyTaxonomy(context, taxonomy, existing, item.Entry, mayOverwrite);

        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Formats an optional number for the change list. These strings are shown to
    /// the user, so the current culture is the correct choice rather than the
    /// invariant one — stated explicitly so it reads as a decision.
    /// </summary>
    private static string Display(int? value, string whenMissing) =>
        value?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? whenMissing;

    /// <summary>
    /// Resolves an entry to a title through any identifier it supplies.
    /// </summary>
    /// <remarks>
    /// Re-resolved at commit rather than trusting the id captured during preview,
    /// which is what makes committing the same preview twice a no-op instead of a
    /// unique-index violation. Identifiers are tried in the order the parser
    /// supplied them; a set that resolves to two different titles was already
    /// turned into a conflict during preview, so the first hit is unambiguous here.
    /// </remarks>
    private static async Task<Anime?> FindExistingAsync(
        AniQueueDbContext context,
        ParsedLibraryEntry entry,
        CancellationToken cancellationToken)
    {
        foreach (var identifier in entry.ExternalIds)
        {
            var animeId = await context.AnimeExternalIds
                .AsNoTracking()
                .Where(x => x.Source == identifier.Source && x.ExternalId == identifier.Value)
                .Select(x => (int?)x.AnimeId)
                .FirstOrDefaultAsync(cancellationToken);

            if (animeId is { } id)
            {
                // Identifiers included because precedence is decided per title, from
                // which sources describe this row.
                return await context.Anime
                    .Include(a => a.ExternalIds)
                    .Include(a => a.Images)
                    .Include(a => a.Genres)
                    .Include(a => a.Studios)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            }
        }

        return null;
    }

    /// <summary>
    /// Adds identifiers the title does not already carry.
    /// </summary>
    /// <remarks>
    /// Two guards, both protecting a unique index that would otherwise abort the
    /// entire import rather than skip one row:
    ///
    /// A title already holding an identifier for that source keeps it. A differing
    /// value means two sources disagree about identity, which is a conflict for the
    /// user rather than something to overwrite silently.
    ///
    /// An identifier already naming a different title is left alone for the same
    /// reason — it is evidence of a duplicate, not permission to re-point it.
    /// </remarks>
    private static async Task EnsureIdentifiersAsync(
        AniQueueDbContext context,
        Anime anime,
        ParsedLibraryEntry entry,
        HashSet<ExternalIdentifier> written,
        CancellationToken cancellationToken)
    {
        foreach (var identifier in entry.ExternalIds)
        {
            if (!written.Add(identifier))
            {
                continue;
            }

            var holdsSource = await context.AnimeExternalIds
                .AnyAsync(
                    x => x.AnimeId == anime.Id && x.Source == identifier.Source,
                    cancellationToken);

            if (holdsSource)
            {
                continue;
            }

            var claimedElsewhere = await context.AnimeExternalIds
                .AnyAsync(
                    x => x.Source == identifier.Source && x.ExternalId == identifier.Value,
                    cancellationToken);

            if (claimedElsewhere)
            {
                continue;
            }

            context.AnimeExternalIds.Add(new AnimeExternalId
            {
                AnimeId = anime.Id,
                Source = identifier.Source,
                ExternalId = identifier.Value
            });
        }
    }

    private static Anime CreateAnime(ParsedLibraryEntry entry, DateTimeOffset now, TitleLanguage preferredTitle)
    {
        var anime = new Anime
        {
            Title = DisplayTitle(entry, preferredTitle),
            TitleRomaji = entry.TitleRomaji,
            TitleEnglish = entry.TitleEnglish,
            TitleNative = entry.TitleNative,
            Source = entry.Source,
            MediaType = entry.MediaType,
            EpisodeCount = entry.EpisodeCount,
            EpisodeDurationMinutes = entry.EpisodeDurationMinutes,
            ReleaseYear = entry.ReleaseYear,
            Description = entry.Description,
            CreatedAt = now,
            UpdatedAt = now
        };

        ApplyCoverImage(anime, entry);
        return anime;
    }

    /// <summary>
    /// Records where the source says this title's cover is.
    /// </summary>
    /// <remarks>
    /// <b>Not routed through <c>Merge</c>, and that is the point of the table.</b>
    /// Every other catalogue field is guarded by precedence because two sources
    /// describe one column and the poorer one must not erase the richer. A picture
    /// is not like that: the row is keyed by the source that published it, so
    /// AniList's poster and MyAnimeList's poster could never have been the same
    /// storage to fight over. Which is why this can be *corrected* — the column it
    /// replaced could not, because a value already stored always won, so pointing it
    /// at a different image size would have updated titles arriving afterwards and
    /// left the whole existing library holding the old address.
    ///
    /// A changed URL means AniList replaced the art — the address carries their own
    /// content hash — so the failure state is cleared and the job will fetch it
    /// again. What is already cached stays cached and stays rendering until it does.
    /// </remarks>
    private static void ApplyCoverImage(Anime anime, ParsedLibraryEntry entry)
    {
        ApplyCoverImage(anime, entry, ImageRendition.Thumbnail, entry.CoverImageUrl);
        ApplyCoverImage(anime, entry, ImageRendition.Full, entry.CoverImageFullUrl);
    }

    /// <summary>
    /// Records one size of one title's cover.
    /// </summary>
    /// <remarks>
    /// Called once per rendition, and the two are entirely independent from here on:
    /// each has its own row, its own fetch, its own retry count and its own
    /// failure state, so a full-size cover that has not arrived does not hold up the
    /// thumbnail that has, and a title showing a list thumbnail with no dialog poster
    /// is a normal intermediate state rather than a fault.
    /// </remarks>
    private static void ApplyCoverImage(
        Anime anime,
        ParsedLibraryEntry entry,
        ImageRendition rendition,
        string? remoteUrl)
    {
        if (remoteUrl is not { Length: > 0 } url)
        {
            return;
        }

        var existing = anime.Images.FirstOrDefault(i =>
            i.Kind == ImageKind.Poster && i.Source == entry.Source && i.Rendition == rendition);

        if (existing is null)
        {
            anime.Images.Add(new AnimeImage
            {
                Kind = ImageKind.Poster,
                Source = entry.Source,
                Rendition = rendition,
                RemoteUrl = url
            });

            return;
        }

        if (string.Equals(existing.RemoteUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        existing.RemoteUrl = url;
        existing.FailedAt = null;
        existing.FailureIsPermanent = false;
        existing.AttemptCount = 0;
    }

    /// <summary>
    /// Every genre and studio already known, by name, for the length of one commit.
    /// </summary>
    /// <remarks>
    /// Loaded once rather than queried per title. Both vocabularies are shared
    /// across the whole library — a few dozen genres and a few thousand studios for
    /// hundreds of titles — so resolving a name against the database per entry would
    /// be thousands of round trips to answer the same handful of questions. Rows
    /// created during the commit are added here as they are created, which is what
    /// stops the second title carrying a brand-new genre inserting it a second time
    /// and violating the unique index.
    /// </remarks>
    private sealed class TaxonomyCache
    {
        private TaxonomyCache(Dictionary<string, Genre> genres, Dictionary<string, Studio> studios)
        {
            Genres = genres;
            Studios = studios;
        }

        public Dictionary<string, Genre> Genres { get; }

        public Dictionary<string, Studio> Studios { get; }

        public static async Task<TaxonomyCache> LoadAsync(
            AniQueueDbContext context,
            CancellationToken cancellationToken) =>
            new(
                await context.Genres.ToDictionaryAsync(
                    g => g.Name, StringComparer.OrdinalIgnoreCase, cancellationToken),
                await context.Studios.ToDictionaryAsync(
                    s => s.Name, StringComparer.OrdinalIgnoreCase, cancellationToken));
    }

    /// <summary>
    /// Applies the genres and studios this source credits.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ApplyCatalogueFields"/> because it needs the
    /// database, not because it obeys different rules — it obeys exactly the same
    /// ones, restated for a shape <c>Merge</c> cannot express.
    ///
    /// <c>Merge</c> rests on "a source never erases a value by not carrying it", and a
    /// set has no null to carry that meaning. So the rule is written again here: an
    /// empty incoming set is <i>silence</i> and changes nothing. Without it, importing
    /// a MyAnimeList export — which publishes no genres at all — would strip the
    /// genres off every title the two sources share, and nothing in a build or a green
    /// suite would notice.
    /// </remarks>
    private static void ApplyTaxonomy(
        AniQueueDbContext context,
        TaxonomyCache cache,
        Anime anime,
        ParsedLibraryEntry entry,
        bool mayOverwrite)
    {
        ApplyGenres(context, cache, anime, entry, mayOverwrite);
        ApplyStudios(context, cache, anime, entry, mayOverwrite);
    }

    private static void ApplyGenres(
        AniQueueDbContext context,
        TaxonomyCache cache,
        Anime anime,
        ParsedLibraryEntry entry,
        bool mayOverwrite)
    {
        // Silence, or a source that may only fill a gap and there is no gap.
        if (entry.Genres.Count == 0 || !(mayOverwrite || anime.Genres.Count == 0))
        {
            return;
        }

        var desired = new List<Genre>(entry.Genres.Count);

        foreach (var name in entry.Genres)
        {
            if (!cache.Genres.TryGetValue(name, out var genre))
            {
                genre = new Genre { Name = name };
                context.Genres.Add(genre);
                cache.Genres[name] = genre;
            }

            desired.Add(genre);
        }

        // Replacement rather than union, so that a genre AniList has removed from a
        // title actually goes. A union would only ever grow the set, which means a
        // mis-tagged title could be corrected at the source and never here.
        foreach (var link in anime.Genres.ToList())
        {
            if (!desired.Any(g => g.Id != 0 && g.Id == link.GenreId))
            {
                anime.Genres.Remove(link);
            }
        }

        foreach (var genre in desired)
        {
            // A genre created moments ago has no id yet, so it cannot already be
            // linked — and asking by id would match every other unsaved row.
            if (genre.Id == 0 || anime.Genres.All(l => l.GenreId != genre.Id))
            {
                anime.Genres.Add(new AnimeGenre { Anime = anime, Genre = genre });
            }
        }
    }

    private static void ApplyStudios(
        AniQueueDbContext context,
        TaxonomyCache cache,
        Anime anime,
        ParsedLibraryEntry entry,
        bool mayOverwrite)
    {
        if (entry.Studios.Count == 0 || !(mayOverwrite || anime.Studios.Count == 0))
        {
            return;
        }

        var desired = new List<(Studio Studio, bool IsMain)>(entry.Studios.Count);

        foreach (var credited in entry.Studios)
        {
            if (!cache.Studios.TryGetValue(credited.Name, out var studio))
            {
                studio = new Studio { Name = credited.Name };
                context.Studios.Add(studio);
                cache.Studios[credited.Name] = studio;
            }

            // A fact about the company rather than this pairing, so it is refreshed
            // from whichever title mentioned it most recently. Every title crediting
            // a company agrees about what kind of company it is, so there is nothing
            // here for two sources to fight over.
            studio.IsAnimationStudio = credited.IsAnimationStudio;

            desired.Add((studio, credited.IsMain));
        }

        foreach (var link in anime.Studios.ToList())
        {
            if (!desired.Any(d => d.Studio.Id != 0 && d.Studio.Id == link.StudioId))
            {
                anime.Studios.Remove(link);
            }
        }

        foreach (var (studio, isMain) in desired)
        {
            var link = studio.Id == 0
                ? null
                : anime.Studios.FirstOrDefault(l => l.StudioId == studio.Id);

            if (link is null)
            {
                anime.Studios.Add(new AnimeStudio { Anime = anime, Studio = studio, IsMain = isMain });
                continue;
            }

            // Which company is the main one is the part of this pairing that can
            // change without the pairing itself changing — a title recredited to a
            // different studio keeps both companies and moves the flag.
            link.IsMain = isMain;
        }
    }

    /// <summary>
    /// Writes what a source says about the title itself, subject to whether it is
    /// allowed to overwrite what another source already said.
    /// </summary>
    /// <remarks>
    /// Filling a gap and settling a disagreement are different things, and
    /// <paramref name="mayOverwrite"/> splits them. A source that outranks every
    /// other source describing this title writes everything. One that does not may
    /// only fill in what is missing — so a title's media type does not depend on
    /// which import ran most recently.
    /// </remarks>
    private static void ApplyCatalogueFields(
        Anime anime,
        ParsedLibraryEntry entry,
        DateTimeOffset now,
        TitleLanguage preferredTitle,
        bool mayOverwrite)
    {
        // Variants first, display title after, so the title is resolved from the
        // merged row rather than from the incoming entry. A MyAnimeList export has no
        // labelled variants, and resolving from it would overwrite a display title
        // built from AniList's.
        anime.TitleRomaji = Merge(anime.TitleRomaji, entry.TitleRomaji, mayOverwrite);
        anime.TitleEnglish = Merge(anime.TitleEnglish, entry.TitleEnglish, mayOverwrite);
        anime.TitleNative = Merge(anime.TitleNative, entry.TitleNative, mayOverwrite);

        // Resolved from the merged row rather than from either side of it, which is
        // what RewriteDisplayTitlesAsync does when the preference changes. The two
        // now agree by construction instead of by comment.
        anime.Title = TitleSelection.Resolve(
            preferredTitle,
            anime.TitleRomaji,
            anime.TitleEnglish,
            anime.TitleNative,
            mayOverwrite || string.IsNullOrWhiteSpace(anime.Title) ? entry.Title : anime.Title);

        if (entry.MediaType != MediaType.Unknown
            && (mayOverwrite || anime.MediaType == MediaType.Unknown))
        {
            anime.MediaType = entry.MediaType;
        }

        anime.EpisodeCount = Merge(anime.EpisodeCount, entry.EpisodeCount, mayOverwrite);
        anime.EpisodeDurationMinutes = Merge(anime.EpisodeDurationMinutes, entry.EpisodeDurationMinutes, mayOverwrite);
        anime.ReleaseYear = Merge(anime.ReleaseYear, entry.ReleaseYear, mayOverwrite);

        // Stored exactly as the source published it. Through Merge like every
        // other catalogue scalar, which means a title both sources identify with
        // MyAnimeList ranked first keeps whichever synopsis landed first — the same
        // behaviour EpisodeCount and ReleaseYear have always had, and the
        // consistency is worth more than a special case for one field.
        anime.Description = Merge(anime.Description, entry.Description, mayOverwrite);

        ApplyCoverImage(anime, entry);

        anime.UpdatedAt = now;
    }

    /// <summary>
    /// The incoming value if it exists and is allowed to land, otherwise what is
    /// already stored.
    /// </summary>
    /// <remarks>
    /// A source never erases a value by not carrying it, whatever its rank: a
    /// MyAnimeList export knows no episode duration, and reading that silence as
    /// "there isn't one" would lose the answer AniList already gave.
    /// </remarks>
    private static T? Merge<T>(T? existing, T? incoming, bool mayOverwrite) =>
        incoming is not null && (mayOverwrite || existing is null) ? incoming : existing;

    /// <summary>
    /// Whether this source's account of a title beats every other source that also
    /// identifies it.
    /// </summary>
    /// <remarks>
    /// Asked per title rather than globally, because rank only means anything where
    /// two sources describe the same row. A title only one source knows about
    /// is always that source's to correct, whatever its rank — which is what keeps a
    /// single-tracker library, and a re-import of a corrected export, behaving as
    /// they always did.
    ///
    /// <b>A source nobody has configured ranks below one somebody has.</b> The
    /// alternative is a tie, and a tie means last-write-wins — the behaviour this
    /// exists to end. It also matches what the setting means to read: somebody who
    /// went to the Sources page and named a primary said something, and a source they
    /// have never opened has not.
    /// </remarks>
    private static bool OutranksOtherSources(
        Anime anime,
        ParsedLibraryEntry entry,
        IReadOnlyDictionary<AnimeSource, int> precedence)
    {
        var incoming = RankOf(entry.Source, precedence);

        return anime.ExternalIds
            .Select(x => x.Source)
            .Where(source => source != entry.Source)
            .All(source => incoming <= RankOf(source, precedence));
    }

    /// <summary>Configured rank, or one step below primary for a source nobody has set up.</summary>
    private static int RankOf(AnimeSource source, IReadOnlyDictionary<AnimeSource, int> precedence) =>
        precedence.TryGetValue(source, out var rank) ? rank : UnconfiguredRank;

    /// <summary>The rank that means primary. Exactly one source can hold it.</summary>
    private const int PrimaryRank = 0;

    /// <summary>
    /// Where every source that is not the primary sits. One value rather than an
    /// ordering, because the seat is single: the losers tie with each other, which is
    /// last-writer-wins between them.
    /// </summary>
    private const int DemotedRank = 1;

    /// <summary>
    /// Where a source that is not in the map at all sits, for the one caller that
    /// tolerates absence. Equal to <see cref="DemotedRank"/> on purpose — a demoted
    /// source and an unranked one are the same thing once a primary exists.
    /// </summary>
    private const int UnconfiguredRank = DemotedRank;

    /// <summary>
    /// Writes watch progress, leaving every locally curated field alone. The list
    /// of what is *not* assigned here is the point of the method.
    /// </summary>
    private static async Task UpsertLibraryEntryAsync(
        AniQueueDbContext context,
        int profileId,
        int animeId,
        ParsedLibraryEntry parsed,
        DateTimeOffset now,
        IReadOnlyDictionary<AnimeSource, int> precedence,
        CancellationToken cancellationToken)
    {
        var entry = await context.LibraryEntries
            .FirstOrDefaultAsync(e => e.ProfileId == profileId && e.AnimeId == animeId, cancellationToken);

        if (entry is null)
        {
            entry = new LibraryEntry
            {
                ProfileId = profileId,
                AnimeId = animeId,
                DateAdded = now
            };

            context.LibraryEntries.Add(entry);
        }

        entry.LastUpdated = now;

        if (!MayWriteTracking(parsed.Source, entry.LastWrittenBySource, precedence))
        {
            // A lower-ranked source has nothing to say about what the user watched.
            // It still reached here, and its catalogue metadata has already
            // been applied — precedence guards the user's tracking data, not facts
            // about the title.
            return;
        }

        entry.Status = parsed.Status;
        entry.EpisodesWatched = parsed.EpisodesWatched;
        entry.UserScore = parsed.UserScore;
        entry.LastWrittenBySource = parsed.Source;

        // A source that has no date must not clear one already known.
        entry.DateStarted = parsed.DateStarted ?? entry.DateStarted;
        entry.DateCompleted = parsed.DateCompleted ?? entry.DateCompleted;

        // Deliberately untouched: PersonalNotes and every
        // Recommendation* field, along with queue membership held on another
        // table. These are the user's work, not the source's.
    }

    /// <summary>
    /// Whether <paramref name="incoming"/> may overwrite tracking data that
    /// <paramref name="lastWriter"/> recorded.
    /// </summary>
    /// <remarks>
    /// Permissive by default, and deliberately so. Precedence only decides
    /// contested rows — where two different sources both describe one title *and*
    /// both have been given a rank. Anything else is allowed through, which is what
    /// makes this inert for a single-tracker setup: the
    /// behaviour is identical to unconditional last-writer-wins until someone
    /// configures a second source.
    /// </remarks>
    private static bool MayWriteTracking(
        AnimeSource incoming,
        AnimeSource? lastWriter,
        IReadOnlyDictionary<AnimeSource, int> precedence)
    {
        if (lastWriter is not { } previous || previous == incoming)
        {
            return true;
        }

        // An unranked source is not outranked by anything. Silently treating a
        // missing rank as "lowest" would make a first sync unable to write
        // anything, which is the opposite of the intent.
        if (!precedence.TryGetValue(incoming, out var incomingRank) ||
            !precedence.TryGetValue(previous, out var previousRank))
        {
            return true;
        }

        // Lower rank wins; equal ranks fall back to last-writer-wins, because two
        // sources the user has declared equally authoritative give no grounds to
        // prefer either.
        return incomingRank <= previousRank;
    }

    private sealed record AnimeSnapshot(
        int Id,
        AnimeSource Source,
        string Title,
        string? TitleRomaji,
        string? TitleEnglish,
        string? TitleNative,
        MediaType MediaType,
        int? EpisodeCount,
        int? EpisodeDurationMinutes,
        int? ReleaseYear,
        // Whether there is art, not where it is. The preview reports gaining a cover
        // and nothing else — a URL that merely changed is almost always the same
        // picture behind a rotated path, and the comment below on that is what these
        // flags exist to keep true now that the address lives on another table.
        //
        // One flag per rendition, because they are gained independently: a title can
        // have a thumbnail and no full-size cover, and a preview that could not see
        // the difference would call it unchanged and skip it.
        bool HasThumbnail,
        bool HasFullCover,
        // Carried in full rather than as a flag, because a synopsis that has been
        // rewritten is a real change and unlike a rotated cover URL it is never
        // spurious. The cost is bounded by the column's length cap and is smaller
        // in practice than the four title variants above already are.
        string? Description,
        IReadOnlyList<string> Genres,
        IReadOnlyList<string> Studios,
        // Carried alongside the names because which company is the main one can
        // change without the set of companies changing at all — a title recredited
        // from Wit Studio to MAPPA credits both either way. Comparing names alone
        // would call that unchanged, and an unchanged item is never applied.
        string? MainStudio);

    private sealed record EntrySnapshot(
        int AnimeId,
        LibraryStatus Status,
        int EpisodesWatched,
        int? UserScore,
        DateOnly? DateStarted,
        DateOnly? DateCompleted);

    /// <param name="ByIdentifier">Every external identifier in the catalogue, to the title it names.</param>
    /// <param name="Identified">
    /// Titles carrying at least one identifier. The complement is the hand-added
    /// set.
    /// </param>
    private sealed record MatchCandidates(
        IReadOnlyList<AnimeSnapshot> Anime,
        IReadOnlyDictionary<ExternalIdentifier, int> ByIdentifier,
        IReadOnlySet<int> Identified,
        IReadOnlyDictionary<int, EntrySnapshot> Entries);
}

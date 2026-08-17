using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Progress;
using AniQueue.Core.Queue;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Import;

/// <summary>
/// Matches parsed entries against the library and applies them.
///
/// Two things this type is careful about:
///
/// 1. <see cref="PreviewAsync"/> never writes. The user has to see the consequence
///    and confirm before anything changes.
/// 2. An import brings catalogue data and watch progress. It never touches what
///    the user curated here — notes, queue position, franchise membership, hidden
///    flag, recommendation data. Re-importing an export must not undo an evening
///    spent organising the backlog.
///
/// The one thing an import does change about the queue is which slots are still
/// needed, and it does that by asking the queue rather than by editing it — see
/// the advancement step at the end of <see cref="CommitAsync"/> (D12).
/// </summary>
public sealed class ImportService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IQueueService queueService,
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

        logger.LogInformation("Import preview started using {Format}", parser.FormatName);

        progress?.Report(new OperationProgress($"Reading the {parser.FormatName} file"));

        var parsed = await parser.ParseAsync(input, cancellationToken);

        if (parsed.IsFileRejected)
        {
            logger.LogWarning("Import file rejected by {Format} parser", parser.FormatName);
            return ImportPreview.Rejected(parser.FormatName, parsed.Problems);
        }

        progress?.Report(new OperationProgress(
            $"Read {parsed.Entries.Count} {(parsed.Entries.Count == 1 ? "entry" : "entries")}"));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        progress?.Report(new OperationProgress("Comparing against your library"));
        var library = await LoadMatchCandidatesAsync(context, profileId, cancellationToken);

        var items = new List<ImportPreviewItem>(parsed.Entries.Count);
        var matched = 0;

        // Identifiers this file has already used, and the title that used them.
        // A file claiming one identifier twice would violate the uniqueness index
        // at commit and abort the whole import, so it is caught here instead and
        // reported against the entry that caused it (D17).
        var claimed = new Dictionary<ExternalIdentifier, string>();

        foreach (var entry in parsed.Entries)
        {
            items.Add(BuildPreviewItem(entry, library, claimed));

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
            FormatName = parser.FormatName,
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
                    var linked = await LinkToExistingAsync(context, item, now, cancellationToken);
                    if (linked is null)
                    {
                        // The record the user chose has since gone. Skipping is safer
                        // than silently creating something they did not ask for.
                        skipped++;
                        continue;
                    }

                    await EnsureIdentifiersAsync(context, linked, item.Entry, written, cancellationToken);
                    await UpsertLibraryEntryAsync(context, profileId, linked.Id, item.Entry, now, cancellationToken);
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
                anime = CreateAnime(item.Entry, now);
                context.Anime.Add(anime);
                await context.SaveChangesAsync(cancellationToken);
                created++;
            }
            else
            {
                ApplyCatalogueFields(anime, item.Entry, now);
                updated++;
            }

            await EnsureIdentifiersAsync(context, anime, item.Entry, written, cancellationToken);
            await UpsertLibraryEntryAsync(context, profileId, anime.Id, item.Entry, now, cancellationToken);
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
                a.Id, a.Source, a.Title, a.MediaType, a.EpisodeCount))
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
        IReadOnlyDictionary<ExternalIdentifier, string> claimed)
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
            // service. That is the whole point of D17: an AniList entry carrying a
            // MyAnimeList id matches a MyAnimeList-imported row instead of
            // duplicating it, and the same holds in the other direction.
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
                return CompareWithExisting(entry, existing, library);
            }

            // No identifier match, but a same-titled entry with no identifier of its
            // own is very likely the title the user added by hand before importing.
            // Creating a second copy would be a silent duplicate, so it is surfaced.
            var manualTwin = library.Anime.FirstOrDefault(a =>
                !library.Identified.Contains(a.Id) &&
                string.Equals(a.Title, entry.Title, StringComparison.OrdinalIgnoreCase));

            if (manualTwin is not null)
            {
                return new ImportPreviewItem
                {
                    Entry = entry,
                    Action = ImportAction.Conflict,
                    ExistingAnimeId = manualTwin.Id,
                    ExistingTitle = manualTwin.Title,
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
        MatchCandidates library)
    {
        var changes = new List<string>();

        if (!string.Equals(existing.Title, entry.Title, StringComparison.Ordinal))
        {
            changes.Add($"Title: '{existing.Title}' → '{entry.Title}'");
        }

        if (existing.MediaType != entry.MediaType && entry.MediaType != MediaType.Unknown)
        {
            changes.Add($"Type: {existing.MediaType} → {entry.MediaType}");
        }

        // Only an actual value replaces a known one; an import that has forgotten
        // the episode count must not erase one already recorded.
        if (entry.EpisodeCount is not null && existing.EpisodeCount != entry.EpisodeCount)
        {
            changes.Add($"Episodes: {Display(existing.EpisodeCount, "unknown")} → {entry.EpisodeCount}");
        }

        // Identifiers this record does not carry yet. Shown because it is a real
        // change and the reason later syncs will match cleanly rather than
        // conflict — this line is D17's bridge being written. Re-importing the
        // same file adds nothing, so idempotency is unaffected.
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
    /// the entry conflicting again on every subsequent import. Franchise grouping on
    /// the existing record is left alone, as with any other import.
    /// </summary>
    /// <remarks>
    /// <see cref="Anime.Source"/> is deliberately not reassigned. It records how the
    /// record came to exist, and a hand-added title that has since been linked was
    /// still hand-added (D17). What changes is which services identify it, and that
    /// is now a separate table — so the backlog's source filter, which asks "is this
    /// on MyAnimeList", finds it either way.
    /// </remarks>
    private static async Task<Anime?> LinkToExistingAsync(
        AniQueueDbContext context,
        ImportPreviewItem item,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (item.ExistingAnimeId is not { } existingId)
        {
            return null;
        }

        var existing = await context.Anime.FirstOrDefaultAsync(a => a.Id == existingId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        ApplyCatalogueFields(existing, item.Entry, now);

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
                return await context.Anime.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
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

    private static Anime CreateAnime(ParsedLibraryEntry entry, DateTimeOffset now) => new()
    {
        Title = entry.Title,
        Source = entry.Source,
        MediaType = entry.MediaType,
        EpisodeCount = entry.EpisodeCount,
        CreatedAt = now,
        UpdatedAt = now
    };

    /// <summary>
    /// Refreshes catalogue metadata only. Franchise membership and ordering are
    /// the user's grouping decisions and are never touched by an import.
    /// </summary>
    private static void ApplyCatalogueFields(Anime anime, ParsedLibraryEntry entry, DateTimeOffset now)
    {
        anime.Title = entry.Title;

        if (entry.MediaType != MediaType.Unknown)
        {
            anime.MediaType = entry.MediaType;
        }

        if (entry.EpisodeCount is not null)
        {
            anime.EpisodeCount = entry.EpisodeCount;
        }

        anime.UpdatedAt = now;
    }

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

        entry.Status = parsed.Status;
        entry.EpisodesWatched = parsed.EpisodesWatched;
        entry.UserScore = parsed.UserScore;
        entry.LastUpdated = now;

        // A source that has no date must not clear one already known.
        entry.DateStarted = parsed.DateStarted ?? entry.DateStarted;
        entry.DateCompleted = parsed.DateCompleted ?? entry.DateCompleted;

        // Deliberately untouched: PersonalNotes, IsHidden and every
        // Recommendation* field, along with queue membership and franchise grouping
        // held on other tables. These are the user's work, not the source's.
    }

    private sealed record AnimeSnapshot(
        int Id,
        AnimeSource Source,
        string Title,
        MediaType MediaType,
        int? EpisodeCount);

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
    /// set, which used to be recognisable by a null identifier column.
    /// </param>
    private sealed record MatchCandidates(
        IReadOnlyList<AnimeSnapshot> Anime,
        IReadOnlyDictionary<ExternalIdentifier, int> ByIdentifier,
        IReadOnlySet<int> Identified,
        IReadOnlyDictionary<int, EntrySnapshot> Entries);
}

using AniQueue.Core.Domain;
using AniQueue.Core.Import;
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
/// </summary>
public sealed class ImportService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ILogger<ImportService> logger) : IImportService
{
    public async Task<ImportPreview> PreviewAsync(
        Stream input,
        IAnimeListParser parser,
        int profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parser);

        logger.LogInformation("Import preview started using {Format}", parser.FormatName);

        var parsed = await parser.ParseAsync(input, cancellationToken);

        if (parsed.IsFileRejected)
        {
            logger.LogWarning("Import file rejected by {Format} parser", parser.FormatName);
            return ImportPreview.Rejected(parser.FormatName, parsed.Problems);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var library = await LoadMatchCandidatesAsync(context, profileId, cancellationToken);

        var items = parsed.Entries
            .Select(entry => BuildPreviewItem(entry, library))
            .ToList();

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (preview.IsFileRejected)
        {
            throw new InvalidOperationException("A rejected import cannot be committed.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var item in preview.Items)
        {
            if (item.Action is ImportAction.Conflict or ImportAction.Unchanged)
            {
                skipped++;
                continue;
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

            await UpsertLibraryEntryAsync(context, profileId, anime.Id, item.Entry, now, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Import committed: {Created} created, {Updated} updated, {Skipped} skipped",
            created,
            updated,
            skipped);

        return new ImportCommitResult { Created = created, Updated = updated, Skipped = skipped };
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
                a.Id, a.Source, a.SourceAnimeId, a.Title, a.MediaType, a.EpisodeCount))
            .ToListAsync(cancellationToken);

        var entries = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId)
            .Select(e => new EntrySnapshot(
                e.AnimeId, e.Status, e.EpisodesWatched, e.UserScore, e.DateStarted, e.DateCompleted))
            .ToDictionaryAsync(e => e.AnimeId, cancellationToken);

        return new MatchCandidates(anime, entries);
    }

    private static ImportPreviewItem BuildPreviewItem(ParsedLibraryEntry entry, MatchCandidates library)
    {
        if (entry.SourceAnimeId is not null)
        {
            var bySourceId = library.Anime.FirstOrDefault(a =>
                a.Source == entry.Source &&
                string.Equals(a.SourceAnimeId, entry.SourceAnimeId, StringComparison.Ordinal));

            if (bySourceId is not null)
            {
                return CompareWithExisting(entry, bySourceId, library);
            }

            // No identifier match, but a same-titled entry with no identifier of its
            // own is very likely the title the user added by hand before importing.
            // Creating a second copy would be a silent duplicate, so it is surfaced.
            var manualTwin = library.Anime.FirstOrDefault(a =>
                a.SourceAnimeId is null &&
                string.Equals(a.Title, entry.Title, StringComparison.OrdinalIgnoreCase));

            if (manualTwin is not null)
            {
                return new ImportPreviewItem
                {
                    Entry = entry,
                    Action = ImportAction.Conflict,
                    ExistingAnimeId = manualTwin.Id,
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
    /// Formats an optional number for the change list. These strings are shown to
    /// the user, so the current culture is the correct choice rather than the
    /// invariant one — stated explicitly so it reads as a decision.
    /// </summary>
    private static string Display(int? value, string whenMissing) =>
        value?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? whenMissing;

    private static Task<Anime?> FindExistingAsync(
        AniQueueDbContext context,
        ParsedLibraryEntry entry,
        CancellationToken cancellationToken) =>
        entry.SourceAnimeId is null
            ? Task.FromResult<Anime?>(null)
            : context.Anime.FirstOrDefaultAsync(
                a => a.Source == entry.Source && a.SourceAnimeId == entry.SourceAnimeId,
                cancellationToken);

    private static Anime CreateAnime(ParsedLibraryEntry entry, DateTimeOffset now) => new()
    {
        Title = entry.Title,
        Source = entry.Source,
        SourceAnimeId = entry.SourceAnimeId,
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

        // Deliberately untouched: PersonalNotes, ManualPriority, IsHidden and every
        // Recommendation* field, along with queue membership and franchise grouping
        // held on other tables. These are the user's work, not the source's.
    }

    private sealed record AnimeSnapshot(
        int Id,
        AnimeSource Source,
        string? SourceAnimeId,
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

    private sealed record MatchCandidates(
        IReadOnlyList<AnimeSnapshot> Anime,
        IReadOnlyDictionary<int, EntrySnapshot> Entries);
}

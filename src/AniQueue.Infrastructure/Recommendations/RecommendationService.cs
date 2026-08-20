using AniQueue.Core.Domain;
using AniQueue.Core.Progress;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Recommendations;

public sealed class RecommendationService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    ScoringResponseParser parser,
    ILogger<RecommendationService> logger,
    TimeProvider? timeProvider = null) : IRecommendationService
{
    // Optional with a real default, the same way the relation backfill takes one:
    // every caller in the application wants the system clock, and only a test wants
    // to say what "now" is.
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ScoringRequest> BuildRequestAsync(
        int profileId,
        ScoringRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ScoringRequestOptions.Default;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var includeNotes = await context.ProfileSettings
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .Select(s => s.IncludePersonalNotesInAiExport)
            .FirstOrDefaultAsync(cancellationToken);

        var candidates = await ReadCandidatesAsync(context, profileId, includeNotes, cancellationToken);

        var scored = context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId
                && e.Status == LibraryStatus.Completed
                && e.UserScore != null);

        var available = await scored.CountAsync(cancellationToken);

        var history = await scored
            // Most recent first, and completion date is what "recent" means for a
            // finished title. Nulls sort last rather than first: an entry with no
            // date is usually an old import, and letting those crowd out dated rows
            // would fill the sample with the least informative half of the library.
            //
            // Ordered on DateOnly and int rather than on DateTimeOffset — SQLite
            // cannot ORDER BY the latter at all, and the entry's key is a stable
            // tiebreak that means the same thing here as "most recently added".
            .OrderByDescending(e => e.DateCompleted != null)
            .ThenByDescending(e => e.DateCompleted)
            .ThenByDescending(e => e.Id)
            .Take(options.MaxHistory)
            .Select(e => new ScoringHistoryEntry
            {
                Title = e.Anime!.Title,
                Score = e.UserScore!.Value,
                MediaType = e.Anime.MediaType,
                Year = e.Anime.ReleaseYear
            })
            .ToListAsync(cancellationToken);

        logger.LogInformation(
            "Built a scoring request for profile {ProfileId}: {Candidates} candidates, {History} of {Available} scored titles.",
            profileId,
            candidates.Count,
            history.Count,
            available);

        return new ScoringRequest
        {
            GeneratedAt = _time.GetUtcNow(),
            Candidates = candidates,
            History = history,
            HistoryAvailable = available
        };
    }

    public async Task<ScoringPreview> PreviewAsync(
        int profileId,
        string json,
        CancellationToken cancellationToken = default)
    {
        var parsed = parser.Parse(json);
        var problems = parsed.Problems.ToList();

        if (parsed.Response is null || parsed.Response.Results.Count == 0)
        {
            return new ScoringPreview { Problems = problems };
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ranked = parsed.Response.Results;
        var rankedIds = ranked.Select(r => r.Id).ToList();

        var known = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && rankedIds.Contains(e.AnimeId))
            .Select(e => new
            {
                e.AnimeId,
                e.Anime!.Title,
                e.Status,
                e.RecommendationScore
            })
            .ToDictionaryAsync(e => e.AnimeId, cancellationToken);

        var candidateCount = await context.LibraryEntries
            .AsNoTracking()
            .CountAsync(
                e => e.ProfileId == profileId && e.Status == LibraryStatus.Planning && !e.IsHidden,
                cancellationToken);

        var items = new List<ScoringPreviewItem>(ranked.Count);

        foreach (var result in ranked)
        {
            if (!known.TryGetValue(result.Id, out var entry))
            {
                // An id naming nothing means the reply is not about this library —
                // a ranking of somebody else's backlog, or a model that invented an
                // id rather than echoing one. Neither is safe to apply the rest of.
                problems.Add(ScoringProblem.Error(
                    $"Rank {result.Rank}: there is no title {result.Id} in your library."));
                continue;
            }

            if (entry.Status != LibraryStatus.Planning)
            {
                // Not an error: the ranking was right when it was made, and the user
                // has simply started watching something since. Skipped rather than
                // written, because a score computed over a backlog says nothing about
                // a row that has left it.
                problems.Add(ScoringProblem.Warning(
                    $"\"{entry.Title}\" is no longer waiting to be watched, so its score is skipped."));
            }

            items.Add(new ScoringPreviewItem
            {
                Result = result,
                Title = entry.Title,
                Status = entry.Status,
                PreviousScore = entry.RecommendationScore
            });
        }

        var preview = new ScoringPreview
        {
            Items = items,
            Problems = problems,
            CandidateCount = candidateCount
        };

        if (preview is { HasErrors: false, MissingCount: > 0 })
        {
            problems.Add(ScoringProblem.Warning(
                $"{preview.MissingCount} of your {candidateCount} waiting titles were not ranked, "
                + "and keep whatever score they already had."));
        }

        return preview with { Problems = problems };
    }

    public async Task<ScoringApplyResult> ApplyAsync(
        int profileId,
        ScoringPreview preview,
        string providerName,
        string? modelIdentifier = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        // Checked here rather than trusted from the caller. The UI disables the
        // button, but the button is not the guard: a preview with an error is a
        // ranking nobody has established the meaning of, and this is the only place
        // that can refuse it for every caller Phase 8 adds.
        if (preview.HasErrors)
        {
            throw new InvalidOperationException(
                "This ranking has errors and cannot be applied, in whole or in part.");
        }

        var applicable = preview.Items.Where(i => i.WillApply).ToList();

        if (applicable.Count == 0)
        {
            throw new InvalidOperationException("This ranking has nothing left to apply.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var now = _time.GetUtcNow();
        var animeIds = applicable.Select(i => i.Result.Id).ToList();

        var entries = await context.LibraryEntries
            .Where(e => e.ProfileId == profileId && animeIds.Contains(e.AnimeId))
            .ToDictionaryAsync(e => e.AnimeId, cancellationToken);

        var completedCount = await context.LibraryEntries
            .CountAsync(
                e => e.ProfileId == profileId
                    && e.Status == LibraryStatus.Completed
                    && e.UserScore != null,
                cancellationToken);

        var run = new RecommendationRun
        {
            ProfileId = profileId,
            CreatedAt = now,
            ProviderName = providerName,
            ModelIdentifier = modelIdentifier,
            CandidateCount = preview.CandidateCount,
            ResultCount = preview.Items.Count,
            CompletedCount = completedCount,
            WasApplied = true
        };

        context.RecommendationRuns.Add(run);

        var applied = 0;
        var skipped = preview.Items.Count - applicable.Count;

        foreach (var item in applicable)
        {
            // Re-read rather than trusted from the preview: the status may have
            // changed between the user seeing it and pressing the button, and this is
            // a single-writer database with a sync running behind it.
            if (!entries.TryGetValue(item.Result.Id, out var entry) || entry.Status != LibraryStatus.Planning)
            {
                skipped++;
                continue;
            }

            run.Items.Add(new RecommendationRunItem
            {
                AnimeId = item.Result.Id,
                Rank = item.Result.Rank,
                PredictedScore = item.Result.PredictedScore,
                Confidence = item.Result.Confidence,
                Reason = item.Result.Reason
            });

            entry.RecommendationScore = item.Result.PredictedScore;
            entry.RecommendationConfidence = item.Result.Confidence;
            entry.RecommendationReason = item.Result.Reason;
            entry.RecommendationUpdatedAt = now;

            // LastUpdated is deliberately untouched. It records when the user's own
            // tracking data last changed, and a model's opinion is not that — moving
            // it would make every entry look freshly edited to anything reading it.

            applied++;

            progress?.Report(new OperationProgress($"Scoring {item.Title}", applied, applicable.Count));
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Applied recommendation run {RunId} for profile {ProfileId}: {Applied} scored, {Skipped} skipped.",
            run.Id,
            profileId,
            applied,
            skipped);

        return new ScoringApplyResult(run.Id, applied, skipped);
    }

    public async Task<IReadOnlyList<RecommendationRunSummary>> GetRunsAsync(
        int profileId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Ordered by key, not by CreatedAt. SQLite cannot ORDER BY a DateTimeOffset —
        // EF stores it as text with an offset and throws at query time rather than
        // returning a wrong order — and for an append-only table the key is the same
        // order anyway (ROADMAP.md §8).
        return await context.RecommendationRuns
            .AsNoTracking()
            .Where(r => r.ProfileId == profileId)
            .OrderByDescending(r => r.Id)
            .Take(take)
            .Select(r => new RecommendationRunSummary
            {
                Id = r.Id,
                CreatedAt = r.CreatedAt,
                ProviderName = r.ProviderName,
                ModelIdentifier = r.ModelIdentifier,
                CandidateCount = r.CandidateCount,
                ResultCount = r.ResultCount,
                CompletedCount = r.CompletedCount,
                WasApplied = r.WasApplied
            })
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<ScoringCandidate>> ReadCandidatesAsync(
        AniQueueDbContext context,
        int profileId,
        bool includeNotes,
        CancellationToken cancellationToken)
    {
        var rows = await context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && e.Status == LibraryStatus.Planning && !e.IsHidden)
            .OrderBy(e => e.Anime!.Title)
            .Select(e => new
            {
                e.AnimeId,
                e.Anime!.Title,
                e.Anime.TitleRomaji,
                e.Anime.TitleEnglish,
                e.Anime.TitleNative,
                e.Anime.MediaType,
                e.Anime.EpisodeCount,
                e.Anime.EpisodeDurationMinutes,
                e.Anime.ReleaseYear,
                Notes = includeNotes ? e.PersonalNotes : null,

                // Projected flat and mapped after materialising, the same way the
                // backlog listing does it: a collection projection is a join, and
                // building the record in the query would depend on constructor
                // translation.
                ExternalIds = e.Anime.ExternalIds
                    .Select(x => new { x.Source, x.ExternalId })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(row => new ScoringCandidate
        {
            Id = row.AnimeId,
            Title = row.Title,
            Titles = new ScoringCandidateTitles
            {
                // The displayed title is already one of these. Repeating it inside the
                // variants would spend context restating what the row above it says.
                Romaji = Distinct(row.TitleRomaji, row.Title),
                English = Distinct(row.TitleEnglish, row.Title),
                Native = Distinct(row.TitleNative, row.Title)
            },
            MediaType = row.MediaType,
            Episodes = row.EpisodeCount,
            EpisodeMinutes = row.EpisodeDurationMinutes,
            Year = row.ReleaseYear,
            ExternalIds = new ScoringCandidateIds
            {
                AniList = row.ExternalIds
                    .FirstOrDefault(x => x.Source == AnimeSource.AniList)?.ExternalId,
                MyAnimeList = row.ExternalIds
                    .FirstOrDefault(x => x.Source == AnimeSource.MyAnimeList)?.ExternalId
            },
            Notes = string.IsNullOrWhiteSpace(row.Notes) ? null : row.Notes
        });
    }

    private static string? Distinct(string? variant, string displayed) =>
        string.IsNullOrWhiteSpace(variant) || string.Equals(variant, displayed, StringComparison.Ordinal)
            ? null
            : variant;
}

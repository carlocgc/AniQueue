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

    public async Task<ScoringHistorySnapshot> BuildHistoryAsync(
        int profileId,
        int? maxHistory,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await ReadHistoryAsync(context, profileId, maxHistory, cancellationToken);
    }

    public async Task<ScoringRequest> BuildRequestAsync(
        int profileId,
        ScoringRequestOptions? options = null,
        ScoringHistorySnapshot? history = null,
        CancellationToken cancellationToken = default)
    {
        var request = await ReadRequestAsync(profileId, options, history, cancellationToken);

        // Logged here rather than in the read below, so the line means what it says: a
        // request somebody asked for and is going to send. <see cref="MeasureAsync"/>
        // builds one too and does not log at this level, because a page rendering a size
        // estimate is not a ranking about to happen (D53).
        logger.LogInformation(
            "Built a scoring request for profile {ProfileId}: {Candidates} candidates, {History} of {Available} scored titles.",
            profileId,
            request.Candidates.Count,
            request.History.Count,
            request.HistoryAvailable);

        return request;
    }

    private async Task<ScoringRequest> ReadRequestAsync(
        int profileId,
        ScoringRequestOptions? options,
        ScoringHistorySnapshot? history,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Everything about how much to send now arrives as an argument. It used to be
        // read from ProfileSettings here, which made this service the owner of a
        // preference as well as the builder of a request; D36 moved the settings to
        // userconfig.json and the caller reads them, so this is a function of what it
        // is given again.
        //
        // The default is what a caller who says nothing gets — notably a test, and
        // notably with notes excluded, which is the answer §6 requires when nobody has
        // opted in.
        options ??= ScoringRequestOptions.Default;

        var waiting = context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && e.Status == LibraryStatus.Planning && !e.IsHidden);

        var candidatesAvailable = await waiting.CountAsync(cancellationToken);

        var candidates = await ReadCandidatesAsync(
            waiting,
            options.IncludePersonalNotes,
            options.MaxCandidates,
            cancellationToken);

        // Read now, or reused from a snapshot the caller took earlier. A sweep passes
        // one so that every batch predicts against the same evidence; everything else
        // passes nothing and reads it here, which is the same behaviour as before.
        var snapshot = history ?? await ReadHistoryAsync(
            context, profileId, options.MaxHistory, cancellationToken);

        return new ScoringRequest
        {
            GeneratedAt = _time.GetUtcNow(),
            Library = await ReadLibraryKeyAsync(context, profileId, cancellationToken),
            Candidates = candidates,
            History = snapshot.Entries,
            HistoryAvailable = snapshot.Available,
            CandidatesAvailable = candidatesAvailable,
            ReturnTop = options.ReturnTop
        };
    }

    private static async Task<ScoringHistorySnapshot> ReadHistoryAsync(
        AniQueueDbContext context,
        int profileId,
        int? maxHistory,
        CancellationToken cancellationToken)
    {
        var scored = context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId
                && e.Status == LibraryStatus.Completed
                && e.UserScore != null);

        var available = await scored.CountAsync(cancellationToken);

        var ordered = scored
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
            .ThenByDescending(e => e.Id);

        // Taken only when there is a cap. Null means every rated title, which is what an
        // empty field on the page now means — and the ordering above still matters there,
        // because it is the order the payload is written in.
        var history = await (maxHistory is { } take ? ordered.Take(take) : ordered)
            .Select(e => new ScoringHistoryEntry
            {
                Title = e.Anime!.Title,
                Score = e.UserScore!.Value,
                MediaType = e.Anime.MediaType,
                Year = e.Anime.ReleaseYear
            })
            .ToListAsync(cancellationToken);

        return new ScoringHistorySnapshot { Entries = history, Available = available };
    }

    public async Task<ScoringSizeEstimate> MeasureAsync(
        int profileId,
        ScoringRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ScoringRequestOptions.Default;

        // Two candidates, once. The one-candidate request the baseline needs is this
        // same request with a shorter list, and re-serialising a record already in
        // memory costs nothing — where asking for it separately cost a second read of
        // every rated title (D53).
        var probe = await ReadRequestAsync(
            profileId,
            options with { MaxCandidates = 2 },
            history: null,
            cancellationToken);

        var baseline = probe with { Candidates = [.. probe.Candidates.Take(1)] };

        var withTwo = ScoringRequestWriter.Write(probe).Length;
        var withOne = ScoringRequestWriter.Write(baseline).Length;

        logger.LogDebug(
            "Measured a scoring request for profile {ProfileId}: {Baseline} characters plus "
            + "{PerCandidate} per title, over {Candidates} waiting and {History} rated.",
            profileId,
            withOne,
            withTwo - withOne,
            probe.CandidatesAvailable,
            probe.HistoryAvailable);

        return new ScoringSizeEstimate
        {
            CandidatesAvailable = probe.CandidatesAvailable,
            HistoryAvailable = probe.HistoryAvailable,

            // Instructions included, because they are sent too and they are not free —
            // the prompt states the scale, the counts and the rules, and on a small
            // model that is context like any other.
            BaselineCharacters = withOne + ScoringPromptBuilder.Build(baseline).Length,

            // Never negative. A backlog of one title makes both writes the same length,
            // and a subtraction that can only be zero should not be able to look like
            // a saving.
            PerCandidateCharacters = Math.Max(withTwo - withOne, 0)
        };
    }

    public async Task<ScoringPreview> PreviewAsync(
        int profileId,
        ScoringRoute route,
        string json,
        ScoringRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = parser.Parse(json);
        var problems = parsed.Problems.ToList();

        if (parsed.Response is null || parsed.Response.Results.Count == 0)
        {
            return new ScoringPreview { Problems = problems };
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Asked before anything is matched, because the answer decides whether matching
        // means anything at all (D50). An id is a row key, and a row key from another
        // database names whatever this one happens to have put at that number — so a
        // reply from elsewhere does not fail loudly, it fails by applying a stranger's
        // scores to titles that were never ranked.
        var libraryKey = await ReadLibraryKeyAsync(context, profileId, cancellationToken);
        var named = parsed.Response.Library;

        if (!string.IsNullOrEmpty(libraryKey))
        {
            // A named database that is not this one, on either route. The endpoint is
            // not expected to say, but a wrong answer to a question nobody asked is
            // still a wrong answer, and refusing it costs nothing.
            if (!string.IsNullOrEmpty(named)
                && !string.Equals(libraryKey, named, StringComparison.OrdinalIgnoreCase))
            {
                // Whole-reply, and one message. Naming the results individually would
                // be both wrong and cruel: they are not individually wrong, they are
                // wholly about something else, and there may be hundreds of them.
                //
                // "AniQueue database" rather than "library", here and below. This
                // codebase already uses "library" for the user's collection — the
                // message right above says "there is no title 815 in your library" —
                // so a second meaning for the same word lands as a claim about their
                // AniList account rather than about a file on disk.
                problems.Add(ScoringProblem.Error(
                    "This reply was built against a different AniQueue database. Every id in it "
                    + "belongs to that one, so none of them are read here. Ask the model again "
                    + "using this installation's own request."));

                return new ScoringPreview { Problems = problems };
            }

            // Silence is refused on the route where a person carried the document.
            //
            // D50 first made this lenient everywhere, reasoning that models drop the
            // envelope and that refusing a correct ranking over a field carrying no
            // ranking costs the user everything and protects nothing. That lost, and
            // the argument that beat it is about who is being protected: leniency is a
            // permanent silent path for every future user of every future version, and
            // "some user will eventually do this" is the right standard for a rule
            // whose whole job is to stop a wrong reply. A refusal costs one retry.
            // Accepting a wrong reply costs a library of scores nobody can tell apart
            // from correct ones afterwards, which is D31's own reasoning one level up.
            //
            // An explicit "yes, this is the right database" confirmation was considered
            // in its place and declined. It asks the user to assert what only the
            // request can establish, the honest expectation is that they would tick it
            // every time, and the question cannot even be phrased without the word
            // "library" meaning two things at once.
            if (string.IsNullOrEmpty(named) && route == ScoringRoute.Pasted)
            {
                problems.Add(ScoringProblem.Error(
                    "This reply does not say which AniQueue database it was built against, so "
                    + "nothing here can tell whether its ids belong to this one. Ask the model "
                    + "again and keep the \"aniqueue\" block at the top of its answer, or add "
                    + $"this line to that block yourself: \"library\": \"{libraryKey}\""));

                return new ScoringPreview { Problems = problems };
            }
        }

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

        // What the request asked about. Absent when the caller no longer holds it, in
        // which case the visible backlog is the best available answer and is what
        // this meant before either limit could make the two differ.
        var offered = request?.Candidates.Select(c => c.Id).ToHashSet();

        var candidateCount = offered?.Count ?? await context.LibraryEntries
            .AsNoTracking()
            .CountAsync(
                e => e.ProfileId == profileId && e.Status == LibraryStatus.Planning && !e.IsHidden,
                cancellationToken);

        // How many rankings a complete reply holds, which is what "missing" is
        // measured against. Without a request in hand it is one per offered title,
        // because that is what an unlimited request asks for.
        var expectedCount = request?.ExpectedResults ?? candidateCount;

        var items = new List<ScoringPreviewItem>(ranked.Count);
        var unmatched = new List<string>();
        var leftBacklog = new List<string>();
        var unasked = new List<string>();
        var position = 0;

        foreach (var result in ranked)
        {
            position++;

            if (!known.TryGetValue(result.Id, out var entry))
            {
                // An id naming nothing means the reply is not about this library —
                // a ranking of somebody else's backlog, or a model that invented an
                // id rather than echoing one. Neither is safe to apply the rest of.
                //
                // Named by position in the reply rather than by rank (D43). The reply
                // is what the user would have to open to find the offending entry;
                // the rank never was, and no longer exists. The same wording the
                // parser uses for its own per-result problems, so two halves of one
                // validation pass do not count differently.
                //
                // Gathered rather than reported one by one (D50). Every one of these
                // has the same cause, and a reply generated against a different library
                // produces one per result — two hundred and fifty identical sentences
                // that say a single thing, in a panel the user has to scroll to reach
                // the button. Summarised below instead.
                unmatched.Add($"Result {position}: there is no title {result.Id} in your library.");
                continue;
            }

            string? skipped = null;

            if (entry.Status != LibraryStatus.Planning)
            {
                // Not an error: the ranking was right when it was made, and the user
                // has simply started watching something since. Skipped rather than
                // written, because a score computed over a backlog says nothing about
                // a row that has left it.
                skipped = "no longer waiting to be watched";

                // Gathered and capped for the same reason the unmatched ids are, and
                // found the same way (D50). A reply built against a replaced database
                // lands most of its ids on rows that were never candidates, so this
                // was seen twenty-four deep, burying the errors above it. Three of
                // these is news. Twenty-four is a wall.
                leftBacklog.Add(
                    $"\"{entry.Title}\" is no longer waiting to be watched, so its score is skipped.");
            }
            else if (offered is not null && !offered.Contains(result.Id))
            {
                // A real title, in the backlog, that this request did not ask about.
                // Either the model reached past what it was given, or an older reply
                // is being pasted against a newer request. Warned rather than
                // rejected — the ranking of what *was* asked for is unaffected — and
                // skipped, because a score for a title that was never in the question
                // was not computed against the same set as the rest.
                skipped = "was not part of this request";

                unasked.Add($"\"{entry.Title}\" was not in the request, so its score is skipped.");
            }

            items.Add(new ScoringPreviewItem
            {
                Result = result,
                Title = entry.Title,
                Status = entry.Status,
                PreviousScore = entry.RecommendationScore,
                SkippedBecause = skipped
            });
        }

        ReportUnmatched(unmatched, ranked.Count, problems);

        ReportCapped(
            leftBacklog,
            problems,
            rest => $"{rest} further title(s) are no longer waiting to be watched, so their "
                + "scores are skipped too.");

        ReportCapped(
            unasked,
            problems,
            rest => $"{rest} further title(s) were not in the request, so their scores are "
                + "skipped too.");

        var preview = new ScoringPreview
        {
            Items = items,
            Problems = problems,
            CandidateCount = candidateCount,
            ExpectedCount = expectedCount
        };

        if (preview is { HasErrors: false, MissingCount: > 0 })
        {
            problems.Add(ScoringProblem.Warning(
                $"{preview.MissingCount} of the {expectedCount} rankings asked for did not come "
                + "back, and those titles keep whatever score they already had."));
        }
        else if (request is { IsRankingLimited: true } && preview.ApplicableCount > expectedCount)
        {
            // Over-delivery, which is a real and common answer: a capable model asked
            // for the best fifty often returns all of them anyway. Everything it sent
            // is valid and all of it applies, so this is not a problem with the reply —
            // it is said because the alternative is silence, and silence here reads as
            // the setting having done nothing. Somebody who set the limit to keep the
            // reply short is owed the news that it was ignored.
            problems.Add(ScoringProblem.Warning(
                $"The model returned {preview.ApplicableCount} rankings when {expectedCount} were "
                + "asked for. They are all valid, and all of them will be applied."));
        }

        return preview with { Problems = problems };
    }

    /// <summary>
    /// How many problems of one kind are named one by one before the rest are counted
    /// (D50).
    /// </summary>
    /// <remarks>
    /// Five is enough to see the shape of the problem — which positions, which ids,
    /// which titles — and few enough that the panel stays readable. The rest are counted
    /// rather than dropped: the number is the useful part once the examples have made
    /// the point.
    /// </remarks>
    private const int ProblemsShown = 5;

    /// <summary>
    /// Reports ids that named nothing, as few problems as will carry the information.
    /// </summary>
    /// <remarks>
    /// These were one error each until D50, which is correct per result and unusable in
    /// aggregate: the case that produces them is a reply built against a library that
    /// no longer exists, and it produces one for every result in the reply.
    ///
    /// <b>All of them unmatched is a different statement from some of them.</b> A reply
    /// where nothing matches is not a reply with many bad ids; it is a reply about
    /// another library, said without an envelope to say it with — so it gets one
    /// sentence that says that, rather than a truncated list of a fact that was never
    /// about individual results. It stays an error either way: D31 applies nothing in
    /// part, and an id naming nothing is exactly as unsafe as it was before.
    /// </remarks>
    private static void ReportUnmatched(
        List<string> unmatched,
        int rankedCount,
        List<ScoringProblem> problems)
    {
        if (unmatched.Count == 0)
        {
            return;
        }

        if (unmatched.Count == rankedCount)
        {
            problems.Add(ScoringProblem.Error(
                $"None of the {rankedCount} rankings name a title in your library. The reply was "
                + "built against a different AniQueue database, most likely one that has since "
                + "been replaced."));

            return;
        }

        foreach (var message in unmatched.Take(ProblemsShown))
        {
            problems.Add(ScoringProblem.Error(message));
        }

        if (unmatched.Count > ProblemsShown)
        {
            problems.Add(ScoringProblem.Error(
                $"{unmatched.Count - ProblemsShown} further result(s) named titles that are not in "
                + "your library."));
        }
    }

    /// <summary>
    /// Reports one kind of skipped title, naming a few and counting the rest (D50).
    /// </summary>
    /// <remarks>
    /// A warning per skipped title is right when a handful of things have moved on since
    /// the ranking was made, which is the case this was written for. It is wrong at the
    /// volume a mismatched database produces, where the same sentence repeats far enough
    /// down the panel to hide the errors above it.
    ///
    /// One kind at a time, rather than one summary over both: a title that has left the
    /// backlog and a title that was never asked about are different facts, and a count
    /// that merged them would answer neither question.
    /// </remarks>
    private static void ReportCapped(
        List<string> messages,
        List<ScoringProblem> problems,
        Func<int, string> summarise)
    {
        foreach (var message in messages.Take(ProblemsShown))
        {
            problems.Add(ScoringProblem.Warning(message));
        }

        if (messages.Count > ProblemsShown)
        {
            problems.Add(ScoringProblem.Warning(summarise(messages.Count - ProblemsShown)));
        }
    }

    /// <summary>
    /// The key naming this profile's library, or null on a database old enough not to
    /// have been given one yet (D50).
    /// </summary>
    /// <remarks>
    /// Read from the database on every use rather than cached or taken from the request
    /// in hand. It changes once, at the moment the row is created, and a request is
    /// exactly the document whose provenance is in question — trusting it to say which
    /// library it belongs to would be asking the suspect for an alibi.
    /// </remarks>
    private static async Task<string?> ReadLibraryKeyAsync(
        AniQueueDbContext context,
        int profileId,
        CancellationToken cancellationToken) =>
        await context.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => p.LibraryKey)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ScoringApplyResult> ApplyAsync(
        int profileId,
        ScoringPreview preview,
        string providerName,
        string? modelIdentifier = null,
        IProgress<OperationProgress>? progress = null,
        TimeSpan? duration = null,
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
            WasApplied = true,
            DurationMilliseconds = duration is { } elapsed ? (long)elapsed.TotalMilliseconds : null
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

            // One message for the whole loop, with the count doing the moving — the
            // same shape the importer uses. A message per title instead reads as a
            // finished step each time it changes, so a ranking of a couple of hundred
            // rows builds a couple of hundred "completed steps" in the dialog, which
            // is a log rather than progress and is nobody's idea of reassurance.
            progress?.Report(new OperationProgress("Applying the ranking", applied, applicable.Count));
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

    public async Task<RecommendationDetail?> GetDetailAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Ordered by run key, not CreatedAt: SQLite cannot ORDER BY a DateTimeOffset
        // and throws at query time. Runs are append-only, so the key is the same
        // order.
        //
        // Applied runs only. A run that was previewed and abandoned never wrote the
        // score this is explaining, so offering its reasoning would explain the
        // number with a sentence from somewhere else.
        return await context.RecommendationRunItems
            .AsNoTracking()
            .Where(i => i.AnimeId == animeId && i.Run!.ProfileId == profileId && i.Run.WasApplied)
            .OrderByDescending(i => i.RunId)
            .Select(i => new RecommendationDetail
            {
                PredictedScore = i.PredictedScore,
                Confidence = i.Confidence,
                Reason = i.Reason,
                DeterminedAt = i.Run!.CreatedAt,
                ProviderName = i.Run.ProviderName,
                ModelIdentifier = i.Run.ModelIdentifier
            })
            .FirstOrDefaultAsync(cancellationToken);
    }


    public async Task<ScoringCoverage> GetCoverageAsync(
        int profileId,
        int staleAfterRatings,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var waiting = context.LibraryEntries
            .AsNoTracking()
            .Where(e => e.ProfileId == profileId && e.Status == LibraryStatus.Planning && !e.IsHidden);

        var total = await waiting.CountAsync(cancellationToken);

        // When each ranked title was scored, read rather than compared in SQL: SQLite
        // can neither order nor compare a DateTimeOffset, so both halves of D39's rule
        // have to be decided here. One nullable column over the ranked part of a
        // backlog is a small thing to carry, and it answers both questions at once.
        var scoredAt = await waiting
            .Where(e => e.RecommendationScore != null)
            .Select(e => e.RecommendationUpdatedAt)
            .ToListAsync(cancellationToken);

        var ranked = scoredAt.Count;

        // The moment before which a score no longer reflects this person's taste (D39).
        //
        // Found as the timestamp of the Nth most recent rating rather than by counting
        // ratings per title: one scalar and one indexed comparison, instead of a
        // correlated subquery for every row in the backlog. The two say the same thing —
        // a score is stale exactly when N titles have been rated since it was written.
        // Sorted here rather than in SQL because SQLite cannot ORDER BY a
        // DateTimeOffset at all — the same limitation BuildRequestAsync works around
        // when it orders history by DateOnly and Id instead. There is no such stand-in
        // for "when was this rated", so the timestamps come back and are ordered in
        // memory: one column of a few hundred rows, against a query that runs when a
        // page is opened.
        var rated = staleAfterRatings <= 0
            ? []
            : await context.LibraryEntries
                .AsNoTracking()
                .Where(e => e.ProfileId == profileId
                    && e.Status == LibraryStatus.Completed
                    && e.UserScore != null)
                .Select(e => e.LastUpdated)
                .ToListAsync(cancellationToken);

        // Nothing is stale until enough has been rated to make it so, which is the
        // right answer for a new library rather than a special case: a backlog scored
        // against three ratings has not been overtaken by anything.
        var staleBefore = staleAfterRatings <= 0 || rated.Count < staleAfterRatings
            ? (DateTimeOffset?)null
            : rated.OrderByDescending(when => when).Skip(staleAfterRatings - 1).First();

        // A score with no timestamp counts as stale. Nothing writes one that way today,
        // but a score whose age cannot be established is exactly the score worth making
        // again — the alternative is a row that can never be picked up and never
        // refreshed.
        var stale = staleBefore is null
            ? 0
            : scoredAt.Count(when => when is null || when < staleBefore);

        return new ScoringCoverage { Waiting = total, Ranked = ranked, Stale = stale };
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
                WasApplied = r.WasApplied,
                Duration = r.DurationMilliseconds == null
                    ? null
                    : TimeSpan.FromMilliseconds(r.DurationMilliseconds.Value)
            })
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<ScoringCandidate>> ReadCandidatesAsync(
        IQueryable<LibraryEntry> waiting,
        bool includeNotes,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (limit is { } take)
        {
            var chosen = await ChooseAsync(waiting, take, cancellationToken);
            waiting = waiting.Where(e => chosen.Contains(e.AnimeId));
        }

        // Title order in the payload either way. Which titles are in it is decided
        // above; the order they are read in is decided here, and a person scanning
        // the request for something they expected to see wants an alphabet.
        var rows = await waiting
            .OrderBy(e => e.Anime!.Title)
            .Select(e => new
            {
                e.AnimeId,
                e.Anime!.Title,
                e.Anime.TitleRomaji,
                e.Anime.TitleEnglish,
                e.Anime.TitleNative,
                e.Anime.MediaType,
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

    /// <summary>
    /// Picks which waiting titles a capped request carries: never scored first, then
    /// whatever was scored longest ago.
    /// </summary>
    /// <remarks>
    /// This is what stops a cap becoming a horizon. Taking the first fifty
    /// alphabetically would leave the second half of a library unranked however many
    /// times it was run; taking the stalest fifty means running it repeatedly sweeps
    /// the whole backlog and then keeps it fresh, so the cap is a page size.
    ///
    /// <b>Sorted in memory, deliberately.</b> SQLite cannot <c>ORDER BY</c> a
    /// <c>DateTimeOffset</c> — EF stores it as text with an offset and throws at
    /// query time — and ordering by the entry key instead would only approximate
    /// recency until everything had been scored once, after which it would return
    /// the same rows forever and quietly stop sweeping. What is materialised is two
    /// columns of the Planning subset, which is the same set the request is drawn
    /// from and is bounded by it.
    /// </remarks>
    private static async Task<HashSet<int>> ChooseAsync(
        IQueryable<LibraryEntry> waiting,
        int take,
        CancellationToken cancellationToken)
    {
        var stalest = await waiting
            .Select(e => new { e.AnimeId, e.RecommendationUpdatedAt })
            .ToListAsync(cancellationToken);

        return stalest
            .OrderBy(e => e.RecommendationUpdatedAt.HasValue)
            .ThenBy(e => e.RecommendationUpdatedAt ?? DateTimeOffset.MinValue)

            // A stable tiebreak, so two runs over an unscored backlog take the same
            // titles rather than an arbitrary overlap.
            .ThenBy(e => e.AnimeId)
            .Take(take)
            .Select(e => e.AnimeId)
            .ToHashSet();
    }

    private static string? Distinct(string? variant, string displayed) =>
        string.IsNullOrWhiteSpace(variant) || string.Equals(variant, displayed, StringComparison.Ordinal)
            ? null
            : variant;
}

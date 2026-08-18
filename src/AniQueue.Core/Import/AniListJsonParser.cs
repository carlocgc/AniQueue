using System.Text.Json;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Import;

/// <summary>
/// Reads an AniList <c>MediaListCollection</c> GraphQL response.
///
/// Pure, like every parser here (D9): it is handed bytes and returns entries, and
/// knows nothing about HTTP, rate limits or which account they came from. That is
/// what lets the whole of AniList's vocabulary be tested against a committed
/// fixture with no network in sight (§8).
///
/// Everything in the response is treated as untrusted, for the same reason the
/// MyAnimeList parser does: the values that break an import are rarely malformed,
/// they are well-formed values meaning something other than they appear. AniList's
/// are a <c>score</c> whose scale depends on a server-side conversion, a FuzzyDate
/// whose three components are independently nullable, and an <c>english</c> title
/// that is absent for roughly one title in seven.
/// </summary>
public sealed class AniListJsonParser(ImportLimits? limits = null) : IAnimeListParser
{
    private readonly ImportLimits _limits = limits ?? ImportLimits.Default;

    public string FormatName => "AniList";

    /// <summary>
    /// Parses the response. Every title variant it publishes is carried through;
    /// which one is displayed is decided where the row is written, from the
    /// profile's preference (D22).
    /// </summary>
    /// <remarks>
    /// The parser deliberately has no opinion about language. It used to take one,
    /// which meant an overload the interface could not express and a second DI
    /// registration to reach it — machinery that existed only because the storage
    /// could not tell romaji from English. Now that it can, this is a plain parser
    /// again.
    /// </remarks>
    public async Task<ParseResult> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buffered = await LimitedBuffer.ReadAsync(input, _limits.MaxBytes, cancellationToken);
        if (buffered is null)
        {
            return ParseResult.Rejected(
                $"The response is larger than the {_limits.MaxBytes / (1024 * 1024)} MB limit.");
        }

        await using (buffered)
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(buffered, cancellationToken: cancellationToken);
                return Parse(document.RootElement);
            }
            catch (JsonException ex)
            {
                // A truncated or non-JSON body. Rejected rather than thrown, because
                // a sync must treat an unreadable response as "no information" and
                // leave the library alone — never as an empty list, which is
                // indistinguishable from the user having deleted everything (D19).
                return ParseResult.Rejected($"The response is not valid JSON: {ex.Message}");
            }
        }
    }

    private ParseResult Parse(JsonElement root)
    {
        // GraphQL reports failure inside a 200. An errors array means the response
        // is not a list — it is an explanation of why there is no list — and reading
        // it as zero entries is the single most dangerous misreading available here.
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => Text(e, "message") ?? "unspecified error")
                .Take(3);

            return ParseResult.Rejected($"AniList rejected the request: {string.Join("; ", messages)}");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("MediaListCollection", out var collection) ||
            collection.ValueKind != JsonValueKind.Object)
        {
            return ParseResult.Rejected(
                "The response contains no MediaListCollection. Is this an AniList list response?");
        }

        var entries = new List<ParsedLibraryEntry>();
        var problems = new List<ImportProblem>();

        // AniList lets one entry be filed under its status list and any number of
        // custom lists, so the collection is not guaranteed to be flat. De-duplicating
        // by media id is the parser's job rather than the collection's promise: the
        // library used to verify the API had no custom lists, so 753 entries arriving
        // as 753 distinct ids proves only that it does not happen when none exist.
        var seen = new HashSet<int>();
        var duplicates = 0;
        var recordNumber = 0;

        if (collection.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Array)
        {
            foreach (var list in lists.EnumerateArray())
            {
                if (!list.TryGetProperty("entries", out var listEntries) ||
                    listEntries.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in listEntries.EnumerateArray())
                {
                    recordNumber++;

                    if (entries.Count >= _limits.MaxEntries)
                    {
                        problems.Add(new ImportProblem(
                            $"Stopped after {_limits.MaxEntries} entries; the rest of the response was ignored."));
                        break;
                    }

                    var parsed = MapEntry(entry, recordNumber, seen, ref duplicates, problems);
                    if (parsed is not null)
                    {
                        entries.Add(parsed);
                    }
                }
            }
        }

        if (duplicates > 0)
        {
            // Summarised rather than one line per entry: a user who files their whole
            // list into custom lists would otherwise get a problem report as long as
            // their library, describing something that is working correctly.
            problems.Add(new ImportProblem(
                $"{duplicates} {(duplicates == 1 ? "entry appeared" : "entries appeared")} in more than one "
                + "list; each title was read once."));
        }

        if (recordNumber == 0)
        {
            // Not a rejection. An account with an empty list is a real thing, and the
            // preview showing nothing to do is the honest answer for it. What must
            // never reach here is a *failed* fetch, which is why the paths above
            // reject rather than fall through to this.
            problems.Add(new ImportProblem("The list is empty."));
        }

        return new ParseResult { Entries = entries, Problems = problems };
    }

    private static ParsedLibraryEntry? MapEntry(
        JsonElement entry,
        int recordNumber,
        HashSet<int> seen,
        ref int duplicates,
        List<ImportProblem> problems)
    {
        if (!entry.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new ImportProblem("Skipped: the entry carries no media record.", recordNumber));
            return null;
        }

        if (Number(media, "id") is not { } mediaId)
        {
            problems.Add(new ImportProblem("Skipped: the entry has no AniList id.", recordNumber));
            return null;
        }

        // The query pins type: ANIME, so a manga format never arrives. Checked anyway
        // because the alternative to a cheap guard is manga silently entering an
        // anime backlog.
        var type = Text(media, "type");
        if (type is not null && !string.Equals(type, "ANIME", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new ImportProblem($"Skipped: {type} is not an anime.", recordNumber));
            return null;
        }

        if (!seen.Add(mediaId))
        {
            duplicates++;
            return null;
        }

        string? romaji = null, english = null, native = null;
        if (media.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Object)
        {
            romaji = Text(titles, "romaji");
            english = Text(titles, "english");
            native = Text(titles, "native");
        }

        // Something has to be stored in the required column even when the profile
        // prefers a variant this title lacks. Romaji first because it is the one
        // AniList almost always has, and the one a MyAnimeList library already holds.
        var fallback = romaji ?? english ?? native;
        if (fallback is null)
        {
            problems.Add(new ImportProblem("Skipped: the entry has no title in any language.", recordNumber));
            return null;
        }

        var identifiers = new List<ExternalIdentifier>(2)
        {
            new(AnimeSource.AniList, mediaId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

        // Absent for roughly one title in 125, and its presence is the whole of D17's
        // bridge: it is what matches a MyAnimeList-imported row instead of duplicating
        // it. Its absence is ordinary, not a problem worth reporting.
        if (Number(media, "idMal") is { } malId && malId > 0)
        {
            identifiers.Add(new ExternalIdentifier(
                AnimeSource.MyAnimeList,
                malId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var episodeCount = Positive(media, "episodes");
        var watched = Math.Max(0, Number(entry, "progress") ?? 0);

        // The same contradiction the MyAnimeList parser guards: a watch count beyond
        // the stated total. The count is the user's own record; the total is
        // catalogue metadata, so the total is what gets dropped.
        if (episodeCount is not null && watched > episodeCount)
        {
            problems.Add(new ImportProblem(
                $"Watched {watched} of a stated {episodeCount} episodes; the episode count was ignored.",
                recordNumber,
                fallback));
            episodeCount = null;
        }

        return new ParsedLibraryEntry
        {
            Source = AnimeSource.AniList,
            ExternalIds = identifiers,
            Title = fallback,
            TitleRomaji = romaji,
            TitleEnglish = english,
            TitleNative = native,
            MediaType = MapFormat(Text(media, "format")),
            EpisodeCount = episodeCount,
            EpisodeDurationMinutes = Positive(media, "duration"),
            ReleaseYear = Positive(media, "seasonYear"),
            CoverImageUrl = MapCoverImage(media),
            Status = MapStatus(Text(entry, "status"), fallback, recordNumber, problems),
            EpisodesWatched = watched,
            UserScore = MapScore(entry, fallback, recordNumber, problems),
            DateStarted = MapFuzzyDate(entry, "startedAt"),
            DateCompleted = MapFuzzyDate(entry, "completedAt"),
            TimesRewatched = Math.Max(0, Number(entry, "repeat") ?? 0)
        };
    }


    private static MediaType MapFormat(string? format) => format?.ToUpperInvariant() switch
    {
        // A short-form TV series is still a TV series for every purpose AniQueue
        // has: it is scheduled weekly and it is not a film. Its brevity is already
        // carried by the episode duration, which is the field the runtime filters
        // actually read.
        "TV" or "TV_SHORT" => MediaType.Tv,
        "MOVIE" => MediaType.Movie,
        "OVA" => MediaType.Ova,
        "ONA" => MediaType.Ona,
        "SPECIAL" => MediaType.Special,
        "MUSIC" => MediaType.Music,
        _ => MediaType.Unknown
    };

    private static LibraryStatus MapStatus(
        string? status,
        string title,
        int recordNumber,
        List<ImportProblem> problems)
    {
        switch (status?.ToUpperInvariant())
        {
            case "CURRENT":

            // A re-watch in progress is watching. It is deliberately not Planning:
            // AniQueue observes what the user is doing rather than authoring it
            // (D12), and someone three episodes into a re-watch is watching the show
            // whatever their intent was when they started (D15).
            case "REPEATING":
                return LibraryStatus.Watching;
            case "COMPLETED":
                return LibraryStatus.Completed;
            case "DROPPED":
                return LibraryStatus.Dropped;
            case "PAUSED":
                return LibraryStatus.OnHold;
            case "PLANNING":
            case null:
                return LibraryStatus.Planning;
            default:
                problems.Add(new ImportProblem(
                    $"Unrecognised status '{status}'; treated as Planning.", recordNumber, title));
                return LibraryStatus.Planning;
        }
    }

    /// <summary>
    /// Converts a <c>POINT_100</c> score into AniQueue's 1–10.
    /// </summary>
    /// <remarks>
    /// The query asks for <c>POINT_100</c> and does the last step here, which is a
    /// decision rather than an oversight. AniList users pick one of five scoring
    /// systems and a raw <c>score</c> returns theirs, so an unconverted read gives
    /// 87 for a 100-point user and violates <c>CK_LibraryEntries_UserScoreRange</c>
    /// mid-transaction. Asking the server to convert is right; asking it for
    /// <c>POINT_10</c> is not — it rounds during conversion, so a 100-point user's 4
    /// comes back as 0, indistinguishable from unscored. The scale meant to protect
    /// low scores is the one that destroys them.
    ///
    /// So: zero is excluded first and means unscored, then the division rounds away
    /// from zero — .NET's default is banker's rounding, which would send 8.5 down —
    /// and anything that survives is clamped up to 1. A 4/100 becomes a 1 rather
    /// than vanishing, which matters because a 1 separates a disliked show from an
    /// unrated one, and that distinction is what Phase 9 ranks on.
    /// </remarks>
    private static int? MapScore(
        JsonElement entry,
        string title,
        int recordNumber,
        List<ImportProblem> problems)
    {
        if (!entry.TryGetProperty("score", out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out var score))
        {
            return null;
        }

        if (score <= 0)
        {
            return null;
        }

        if (score > 100)
        {
            problems.Add(new ImportProblem(
                $"Score {score} is above the 100-point scale requested and was ignored.",
                recordNumber,
                title));
            return null;
        }

        return Math.Max(1, (int)Math.Round(score / 10.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Reads a FuzzyDate, which is three independently nullable components.
    /// </summary>
    /// <remarks>
    /// Only a complete date becomes a date. A year alone is real information, but
    /// there is nowhere truthful to put it: <c>DateOnly</c> would have to invent a
    /// month and a day, and "started on 1 January" is a fact the user never stated
    /// and would see rendered as though they had. Null already means "not known"
    /// throughout the pipeline, and <c>0000-00-00</c> from a MyAnimeList export is
    /// treated exactly the same way.
    /// </remarks>
    private static DateOnly? MapFuzzyDate(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var date) || date.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Number(date, "year") is not { } year ||
            Number(date, "month") is not { } month ||
            Number(date, "day") is not { } day)
        {
            return null;
        }

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 31 February and friends. Nothing stops a client writing one.
            return null;
        }
    }

    /// <summary>
    /// Takes the cover image URL, and only if it is one AniQueue could safely render.
    /// </summary>
    /// <remarks>
    /// The value is a remote string that ends up in an <c>img src</c>, so the scheme
    /// is checked rather than assumed: anything that is not absolute http or https
    /// is dropped. Only <c>extraLarge</c> is read — the other sizes exist, nothing
    /// renders an image yet, and a column per size would be storing data ahead of a
    /// feature (§10).
    /// </remarks>
    private static string? MapCoverImage(JsonElement media)
    {
        if (!media.TryGetProperty("coverImage", out var cover) || cover.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = Text(cover, "extraLarge");
        if (url is null)
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>
    /// Zero means "unknown" here as it does in a MyAnimeList export — an unaired
    /// series carries <c>episodes: 0</c> rather than null.
    /// </summary>
    private static int? Positive(JsonElement element, string property) =>
        Number(element, property) is { } value && value > 0 ? value : null;
}

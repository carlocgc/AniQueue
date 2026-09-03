using System.Text.Json;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Import;

/// <summary>
/// Reads an AniList <c>MediaListCollection</c> GraphQL response.
///
/// Pure, like every parser here: it is handed bytes and returns entries, and knows
/// nothing about HTTP, rate limits or which account they came from, so the whole of
/// AniList's vocabulary can be tested against a committed fixture.
///
/// Everything in the response is treated as untrusted. The values that break an
/// import are rarely malformed — they are well-formed values meaning something
/// other than they appear.
/// </summary>
public sealed class AniListJsonParser(ImportLimits? limits = null) : IAnimeListParser
{
    private readonly ImportLimits _limits = limits ?? ImportLimits.Default;

    public string FormatName => "AniList";

    /// <summary>
    /// Parses the response. Every title variant it publishes is carried through;
    /// which one is displayed is decided where the row is written, from the
    /// profile's preference.
    /// </summary>
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
                // Rejected rather than thrown: a sync must treat an unreadable
                // response as "no information" and leave the library alone, never as
                // an empty list.
                return ParseResult.Rejected($"The response is not valid JSON: {ex.Message}");
            }
        }
    }

    private ParseResult Parse(JsonElement root)
    {
        // GraphQL reports failure inside a 200. An errors array is an explanation of
        // why there is no list, and must never be read as zero entries.
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => Text(e, "message") ?? "unspecified error")
                .Take(3);

            return ParseResult.Rejected($"AniList rejected the request: {string.Join("; ", messages)}");
        }

        // The ValueKind check on data is load-bearing: TryGetProperty throws rather
        // than returning false when the element is not an object, so a body of
        // {"data": null} would otherwise leave by exception instead of as a
        // rejection.
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("MediaListCollection", out var collection) ||
            collection.ValueKind != JsonValueKind.Object)
        {
            return ParseResult.Rejected(
                "The response contains no MediaListCollection. Is this an AniList list response?");
        }

        var entries = new List<ParsedLibraryEntry>();
        var problems = new List<ImportProblem>();

        // AniList lets one entry be filed under its status list and any number of
        // custom lists, so the collection is not guaranteed to be flat and the
        // parser de-duplicates by media id.
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
            // their library.
            problems.Add(new ImportProblem(
                $"{duplicates} {(duplicates == 1 ? "entry appeared" : "entries appeared")} in more than one "
                + "list; each title was read once."));
        }

        if (recordNumber == 0)
        {
            // Not a rejection: an account with an empty list is a real thing. A
            // failed fetch must never reach here, which is why the paths above
            // reject rather than fall through.
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
        // so manga cannot silently enter an anime backlog.
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

        // What matches a MyAnimeList-imported row instead of duplicating it. Absent
        // for roughly one title in 125, which is ordinary rather than a problem.
        if (Number(media, "idMal") is { } malId && malId > 0)
        {
            identifiers.Add(new ExternalIdentifier(
                AnimeSource.MyAnimeList,
                malId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var episodeCount = Positive(media, "episodes");
        var watched = Math.Max(0, Number(entry, "progress") ?? 0);

        // A watch count beyond the stated total. The count is the user's own record
        // and the total is catalogue metadata, so the total is what gets dropped.
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
            CoverImageUrl = MapCoverImage(media, "medium"),
            CoverImageFullUrl = MapCoverImage(media, "extraLarge"),
            Description = Text(media, "description"),
            Genres = MapGenres(media),
            Studios = MapStudios(media),
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
        // A short-form TV series is still a TV series here; its brevity is carried
        // by the episode duration, which is what the runtime filters read.
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

            // A re-watch in progress is watching, not planning.
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
    /// The query asks AniList for <c>POINT_100</c> and does the last step here,
    /// because asking it for <c>POINT_10</c> rounds during conversion and turns a
    /// 100-point user's 4 into a 0, indistinguishable from unscored.
    ///
    /// Zero is excluded first and means unscored; the division rounds away from zero
    /// rather than using .NET's banker's rounding; anything that survives is clamped
    /// up to 1, so a low score becomes a 1 rather than vanishing.
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
    /// Only a complete date becomes a date. A partial one would mean inventing a
    /// month and a day the user never stated, and null already means "not known"
    /// throughout the pipeline.
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
    /// One named size of the cover, if the response carries it and it is a URL.
    /// </summary>
    /// <remarks>
    /// The scheme is checked rather than assumed, because this is a remote string
    /// AniQueue will itself request. The host is checked too, but with the code that
    /// makes the request. The size is a parameter rather than two near-identical
    /// methods, so the thumbnail and the full-size cover cannot drift into different
    /// validation.
    /// </remarks>
    private static string? MapCoverImage(JsonElement media, string size)
    {
        if (!media.TryGetProperty("coverImage", out var cover) || cover.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = Text(cover, size);
        if (url is null)
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
    }

    /// <summary>
    /// The genres AniList names for this title, deduplicated and in order. An absent
    /// or empty list is silence: it returns empty, and the merge leaves whatever the
    /// title already has alone.
    /// </summary>
    private static List<string> MapGenres(JsonElement media)
    {
        if (!media.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();

        foreach (var genre in genres.EnumerateArray())
        {
            if (genre.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = genre.GetString()?.Trim();

            if (name is { Length: > 0 } && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Every company credited, with the main-studio flag AniList puts on the edge.
    /// </summary>
    /// <remarks>
    /// The main flag is on the edge and the animation-studio fact is on the node, so
    /// both are read here: AniList returns studios and producers in one
    /// undifferentiated list, and losing either makes it impossible to say afterwards
    /// which was which. Deduplicated by name, with the main claim winning so a
    /// duplicate edge cannot demote the actual studio.
    /// </remarks>
    private static List<ParsedStudio> MapStudios(JsonElement media)
    {
        if (!media.TryGetProperty("studios", out var studios) || studios.ValueKind != JsonValueKind.Object
            || !studios.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<ParsedStudio>();

        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object
                || !edge.TryGetProperty("node", out var node)
                || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Text(node, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var isMain = Flag(edge, "isMain");
            var index = parsed.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                if (isMain && !parsed[index].IsMain)
                {
                    parsed[index] = parsed[index] with { IsMain = true };
                }

                continue;
            }

            parsed.Add(new ParsedStudio(name, isMain, Flag(node, "isAnimationStudio")));
        }

        return parsed;
    }

    /// <summary>
    /// A boolean property, treating anything that is not literally true as false.
    /// </summary>
    /// <remarks>
    /// Absent, null and non-boolean all mean false rather than throwing. These flags
    /// only narrow what is displayed, so a missing one costs a studio line rather
    /// than a sync.
    /// </remarks>
    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

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

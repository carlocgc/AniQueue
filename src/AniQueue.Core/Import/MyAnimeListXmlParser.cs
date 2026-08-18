using System.Globalization;
using System.Xml;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Import;

/// <summary>
/// Reads the standard MyAnimeList XML export.
///
/// Every value in the file is treated as untrusted. MAL's own exports contain
/// values that are not what they appear — <c>0000-00-00</c> for "no date", <c>0</c>
/// for both "unscored" and "episode count unknown" — and hand-edited files contain
/// worse. Nothing here throws on bad input: unusable records are reported and the
/// remaining ones still import, because losing a whole export to one broken row
/// would be a poor trade for the user.
/// </summary>
public sealed class MyAnimeListXmlParser(ImportLimits? limits = null) : IAnimeListParser
{
    private readonly ImportLimits _limits = limits ?? ImportLimits.Default;

    public string FormatName => "MyAnimeList XML";

    public async Task<ParseResult> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buffered = await LimitedBuffer.ReadAsync(input, _limits.MaxBytes, cancellationToken);
        if (buffered is null)
        {
            return ParseResult.Rejected(
                $"The file is larger than the {_limits.MaxBytes / (1024 * 1024)} MB import limit.");
        }

        using (buffered)
        {
            try
            {
                return Parse(buffered);
            }
            catch (XmlException ex)
            {
                // Malformed XML is a user mistake (wrong file, truncated download),
                // not an application fault, so it is reported rather than thrown.
                return ParseResult.Rejected($"The file is not valid XML: {ex.Message}");
            }
        }
    }

    private ParseResult Parse(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            // Defence against XXE and entity-expansion attacks. Prohibiting DTDs
            // outright removes external entity resolution, billion-laughs
            // expansion and external DTD fetches in one step; the null resolver
            // and zero entity budget make that explicit rather than implied.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false
        };

        var entries = new List<ParsedLibraryEntry>();
        var problems = new List<ImportProblem>();
        var recordNumber = 0;

        using var reader = XmlReader.Create(stream, settings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "anime", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            recordNumber++;

            if (entries.Count >= _limits.MaxEntries)
            {
                problems.Add(new ImportProblem(
                    $"Stopped after {_limits.MaxEntries} entries; the rest of the file was ignored."));
                break;
            }

            var fields = ReadFields(reader);
            var title = Value(fields, "series_title");

            if (string.IsNullOrWhiteSpace(title))
            {
                problems.Add(new ImportProblem("Skipped: the entry has no title.", recordNumber));
                continue;
            }

            entries.Add(MapEntry(fields, title, recordNumber, problems));
        }

        if (recordNumber == 0)
        {
            problems.Add(new ImportProblem(
                "No <anime> entries were found. Is this a MyAnimeList anime list export?"));
        }

        return new ParseResult { Entries = entries, Problems = problems };
    }

    /// <summary>Reads one &lt;anime&gt; element's immediate children into a lookup.</summary>
    private static Dictionary<string, string> ReadFields(XmlReader reader)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (reader.IsEmptyElement)
        {
            return fields;
        }

        using var subtree = reader.ReadSubtree();
        subtree.Read();

        while (!subtree.EOF)
        {
            if (subtree.NodeType != XmlNodeType.Element || subtree.Depth == 0)
            {
                if (!subtree.Read())
                {
                    break;
                }

                continue;
            }

            var name = subtree.Name;

            if (subtree.IsEmptyElement)
            {
                fields[name] = string.Empty;
                subtree.Read();
                continue;
            }

            try
            {
                // ReadElementContentAsString consumes the closing tag and leaves the
                // reader on the *next* node. The loop must therefore not call Read()
                // again afterwards, or every second field is silently skipped.
                fields[name] = subtree.ReadElementContentAsString().Trim();
            }
            catch (InvalidOperationException)
            {
                // Nested markup where a plain value was expected. MAL's fields are
                // flat, so this only occurs in hand-edited files; skip the field
                // rather than abandoning an otherwise usable export.
                subtree.Skip();
            }
        }

        return fields;
    }

    private static ParsedLibraryEntry MapEntry(
        Dictionary<string, string> fields,
        string title,
        int recordNumber,
        List<ImportProblem> problems)
    {
        var status = MapStatus(Value(fields, "my_status"), title, recordNumber, problems);
        var episodeCount = PositiveOrNull(Value(fields, "series_episodes"));
        var watched = NonNegative(Value(fields, "my_watched_episodes"));

        // A source that claims more episodes watched than exist is contradicting
        // itself. The watch count is the user's own record and the episode total is
        // catalogue metadata, so the count is kept and the total is not trusted.
        if (episodeCount is not null && watched > episodeCount)
        {
            problems.Add(new ImportProblem(
                $"Watched {watched} of a stated {episodeCount} episodes; the episode count was ignored.",
                recordNumber,
                title));
            episodeCount = null;
        }

        // One identifier, because a MyAnimeList export knows only about
        // MyAnimeList. An AniList response will supply two (D17); nothing
        // downstream needs to know which parser produced how many.
        var malId = NullIfBlank(Value(fields, "series_animedb_id"));

        return new ParsedLibraryEntry
        {
            Source = AnimeSource.MyAnimeList,
            ExternalIds = malId is null ? [] : [new ExternalIdentifier(AnimeSource.MyAnimeList, malId)],
            Title = title,
            MediaType = MapMediaType(Value(fields, "series_type")),
            EpisodeCount = episodeCount,
            Status = status,
            EpisodesWatched = watched,
            UserScore = MapScore(Value(fields, "my_score"), title, recordNumber, problems),
            DateStarted = MapDate(Value(fields, "my_start_date")),
            DateCompleted = MapDate(Value(fields, "my_finish_date")),
            TimesRewatched = NonNegative(Value(fields, "my_times_watched"))
        };
    }

    private static LibraryStatus MapStatus(
        string? raw,
        string title,
        int recordNumber,
        List<ImportProblem> problems)
    {
        // MAL writes these with spaces and hyphens inconsistently across export
        // versions, so compare on letters only rather than chasing every variant.
        var normalised = new string((raw ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();

        switch (normalised)
        {
            case "completed":
                return LibraryStatus.Completed;
            case "watching":
                return LibraryStatus.Watching;
            case "onhold":
                return LibraryStatus.OnHold;
            case "dropped":
                return LibraryStatus.Dropped;
            case "plantowatch":
                return LibraryStatus.Planning;
            case "":
                return LibraryStatus.Planning;
            default:
                problems.Add(new ImportProblem(
                    $"Unrecognised status '{raw}'; treated as Planning.", recordNumber, title));
                return LibraryStatus.Planning;
        }
    }

    private static MediaType MapMediaType(string? raw) =>
        new string((raw ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
        {
            "tv" => MediaType.Tv,
            "movie" => MediaType.Movie,
            "ova" => MediaType.Ova,
            "ona" => MediaType.Ona,
            "special" => MediaType.Special,
            "tvspecial" => MediaType.Special,
            "music" => MediaType.Music,
            _ => MediaType.Unknown
        };

    /// <summary>
    /// MAL uses 0 for "unscored", which must not become a real rating — the
    /// database rejects it, and treating it as a 0/10 would poison every
    /// recommendation built from the user's taste.
    /// </summary>
    private static int? MapScore(
        string? raw,
        string title,
        int recordNumber,
        List<ImportProblem> problems)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) || score == 0)
        {
            return null;
        }

        if (score is < 1 or > 10)
        {
            problems.Add(new ImportProblem(
                $"Score {score} is outside 1-10 and was ignored.", recordNumber, title));
            return null;
        }

        return score;
    }

    /// <summary>
    /// MAL writes <c>0000-00-00</c> for "no date", which is not a representable
    /// date in any calendar. Anything unparseable is treated the same way.
    /// </summary>
    private static DateOnly? MapDate(string? raw) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string? Value(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? PositiveOrNull(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    private static int NonNegative(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
}

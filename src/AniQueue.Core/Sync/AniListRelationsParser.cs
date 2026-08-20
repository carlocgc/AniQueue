using System.Text.Json;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Sync;

/// <summary>One edge as the response stated it, before anything has been stored.</summary>
/// <param name="Type">Already mapped; a type AniQueue does not store never reaches here.</param>
/// <param name="RelatedExternalId">The far end, which is frequently a title the user does not own.</param>
public sealed record ParsedRelation(RelationType Type, string RelatedExternalId);

/// <summary>
/// Everything the relation query returned about one title.
/// </summary>
/// <remarks>
/// The catalogue fields ride along because the request is being made anyway (D25):
/// <see cref="StartDate"/> is what release ordering needs and a year cannot give,
/// and <see cref="CoverImageColor"/> is six bytes that Phase 9 will want.
/// Both are null when the source did not publish them, and null means "leave what
/// is stored alone" — the same rule the import path applies.
/// </remarks>
public sealed record ParsedRelations
{
    public required string ExternalId { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? CoverImageColor { get; init; }

    /// <summary>Empty is a real answer, and the common one. Roughly half a library is standalone.</summary>
    public IReadOnlyList<ParsedRelation> Relations { get; init; } = [];
}

/// <summary>What one relation response contained, or why it could not be read.</summary>
public sealed record RelationParseResult
{
    public IReadOnlyList<ParsedRelations> Titles { get; init; } = [];

    /// <summary>Null when the response was read. Shown to nobody; logged.</summary>
    public string? FailureReason { get; init; }

    public bool Succeeded => FailureReason is null;

    public static RelationParseResult Rejected(string reason) => new() { FailureReason = reason };
}

/// <summary>
/// Reads the relation query's response.
///
/// Pure, like every parser here (D9): handed bytes, returns edges, and knows
/// nothing about HTTP or rate limits — which is what lets AniList's whole relation
/// vocabulary be tested against a committed fixture with no network (§8).
/// </summary>
/// <remarks>
/// It is deliberately stricter than the list parser in one respect and looser in
/// another. Stricter: a relation type AniQueue does not store is dropped silently
/// rather than recorded as a problem, because <c>CHARACTER</c> and <c>ADAPTATION</c>
/// edges are on most titles and reporting them would be reporting the API working
/// (D24). Looser: a title with no relations is not a problem either. Both of those
/// are the normal case, and a parser that complains about the normal case teaches
/// its caller to ignore it.
/// </remarks>
public static class AniListRelationsParser
{
    /// <summary>
    /// The whole response is buffered by the caller before it arrives here, so the
    /// ceiling is enforced there. This exists to keep an absurd body from being
    /// parsed rather than as the real limit.
    /// </summary>
    private const int MaxTitles = 200;

    public static RelationParseResult Parse(ReadOnlySpan<byte> payload)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload.ToArray());
        }
        catch (JsonException ex)
        {
            return RelationParseResult.Rejected($"The response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            return Parse(document.RootElement);
        }
    }

    private static RelationParseResult Parse(JsonElement root)
    {
        // GraphQL reports failure inside a 200, and reading an errors array as "this
        // title has no relations" would mark it fetched and never ask again — a
        // permanent hole written by a transient failure.
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => Text(e, "message") ?? "unspecified error")
                .Take(3);

            return RelationParseResult.Rejected($"AniList rejected the request: {string.Join("; ", messages)}");
        }

        // The ValueKind check is not belt-and-braces: TryGetProperty throws rather
        // than returning false when the element is not an object, and "data": null
        // is what an error response looks like once its errors array has been
        // stripped by something in the middle.
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("Page", out var page) ||
            page.ValueKind != JsonValueKind.Object ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return RelationParseResult.Rejected(
                "The response contains no media page. Is this an AniList relation response?");
        }

        var titles = new List<ParsedRelations>();

        foreach (var element in media.EnumerateArray())
        {
            if (titles.Count >= MaxTitles)
            {
                break;
            }

            if (MapTitle(element) is { } parsed)
            {
                titles.Add(parsed);
            }
        }

        return new RelationParseResult { Titles = titles };
    }

    private static ParsedRelations? MapTitle(JsonElement media)
    {
        if (media.ValueKind != JsonValueKind.Object || Number(media, "id") is not { } id)
        {
            return null;
        }

        return new ParsedRelations
        {
            ExternalId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StartDate = MapFuzzyDate(media, "startDate"),
            CoverImageColor = MapColour(media),
            Relations = MapEdges(media)
        };
    }

    private static List<ParsedRelation> MapEdges(JsonElement media)
    {
        var edges = new List<ParsedRelation>();

        if (!media.TryGetProperty("relations", out var relations) ||
            relations.ValueKind != JsonValueKind.Object ||
            !relations.TryGetProperty("edges", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return edges;
        }

        var seen = new HashSet<(RelationType, string)>();

        foreach (var edge in array.EnumerateArray())
        {
            if (RelationTypes.FromAniList(Text(edge, "relationType")) is not { } type)
            {
                continue;
            }

            if (!edge.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // The query pins type: ANIME on the media it selects, but not on the far
            // end of an edge — a relation node is whatever it is. Manga and novels
            // arrive here and are dropped, which is most of what ADAPTATION and
            // SOURCE would have carried had those types been kept at all.
            if (Text(node, "type") is { } nodeType &&
                !string.Equals(nodeType, "ANIME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Number(node, "id") is not { } relatedId)
            {
                continue;
            }

            var related = relatedId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Two edges of the same type to the same title would violate the unique
            // index on the way in. Dropped here rather than at the database, so the
            // batch does not fail over something the source is entitled to publish.
            if (seen.Add((type, related)))
            {
                edges.Add(new ParsedRelation(type, related));
            }
        }

        return edges;
    }

    /// <summary>
    /// Reads a FuzzyDate, whose three components are independently nullable.
    /// </summary>
    /// <remarks>
    /// A partial date is null rather than guessed. "2016 with no month" could be
    /// filled in as January, and then a series that aired in October would sort
    /// ahead of one that aired in March — an invented fact producing a wrong order,
    /// which is worse than an unknown one sorting last.
    /// </remarks>
    private static DateOnly? MapFuzzyDate(JsonElement media, string property)
    {
        if (!media.TryGetProperty(property, out var date) || date.ValueKind != JsonValueKind.Object)
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
    /// Takes the cover's dominant colour, and only if it is a colour.
    /// </summary>
    /// <remarks>
    /// The value ends up in a style attribute, so it is validated rather than
    /// trusted: <c>#</c> followed by exactly six hex digits, or nothing. This is the
    /// cheapest possible guard against a source string reaching CSS, and the field
    /// is worthless enough that dropping a malformed one costs nothing.
    /// </remarks>
    private static string? MapColour(JsonElement media)
    {
        if (!media.TryGetProperty("coverImage", out var cover) || cover.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var colour = Text(cover, "color");

        if (colour is null || colour.Length != 7 || colour[0] != '#')
        {
            return null;
        }

        foreach (var character in colour.AsSpan(1))
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        return colour.ToLowerInvariant();
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
}

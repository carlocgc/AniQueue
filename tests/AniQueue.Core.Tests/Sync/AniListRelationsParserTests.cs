using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Sync;

namespace AniQueue.Core.Tests.Sync;

/// <summary>
/// AniList's relation vocabulary, against a committed fixture so no test touches
/// the network (§8).
///
/// The cases are chosen from what a real library would <i>not</i> have exercised:
/// declined relation types, a manga on the far end of an edge, a duplicate edge, a
/// partial date and a malformed colour. A capture from a live account would have
/// tested none of them, and every one is a way to write a row that cannot be
/// rendered or an order that is subtly wrong.
/// </summary>
public class AniListRelationsParserTests
{
    private static byte[] Fixture()
    {
        using var stream = typeof(AniListRelationsParserTests).Assembly.GetManifestResourceStream(
            "AniQueue.Core.Tests.Sync.Fixtures.anilist-relations.json");

        if (stream is null)
        {
            throw new InvalidOperationException("The relations fixture is missing from the test assembly.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static RelationParseResult ParseFixture() => AniListRelationsParser.Parse(Fixture());

    private static ParsedRelations Title(RelationParseResult result, string externalId) =>
        result.Titles.Single(t => t.ExternalId == externalId);

    [Fact]
    public void Every_media_in_the_page_is_read()
    {
        var result = ParseFixture();

        Assert.True(result.Succeeded);
        Assert.Equal(["4001", "4002", "4003", "4020", "4021"], result.Titles.Select(t => t.ExternalId));
    }

    [Fact]
    public void Only_relation_types_AniQueue_stores_are_kept()
    {
        // CHARACTER links shows sharing a character and nothing else; OTHER has no
        // meaning to label a row with; ADAPTATION points at manga. All three are on
        // real titles constantly, and none can be rendered (D24).
        var slayers = Title(ParseFixture(), "4001");

        Assert.Equal(
            [RelationType.Sequel, RelationType.SideStory, RelationType.Summary],
            slayers.Relations.Select(r => r.Type));
    }

    [Fact]
    public void A_manga_on_the_far_end_of_an_edge_is_dropped()
    {
        // The query pins type: ANIME on the media it selects but not on a relation
        // node, so this is the only place it can be caught.
        var result = ParseFixture();

        Assert.All(
            result.Titles.SelectMany(t => t.Relations),
            relation => Assert.NotEqual("7777", relation.RelatedExternalId));
    }

    [Fact]
    public void The_same_edge_stated_twice_is_stored_once()
    {
        // It would otherwise violate the unique index and fail a batch of several
        // hundred edges over something the source is entitled to publish.
        var slayers = Title(ParseFixture(), "4001");

        Assert.Single(slayers.Relations, r =>
            r is { Type: RelationType.Sequel, RelatedExternalId: "4002" });
    }

    [Fact]
    public void An_edge_whose_node_has_no_id_is_dropped_without_taking_the_others()
    {
        var third = Title(ParseFixture(), "4003");

        Assert.Equal(
            [RelationType.Prequel, RelationType.Compilation],
            third.Relations.Select(r => r.Type));
    }

    [Fact]
    public void Direction_is_preserved_exactly_as_the_source_stated_it()
    {
        // Both ends of one relationship appear, each from its own perspective. The
        // parser normalises neither, because which end spoke is part of the fact
        // and is what lets a title nobody has fetched be reached through the far
        // end of somebody else's edge (D24).
        var result = ParseFixture();

        Assert.Contains(
            Title(result, "4001").Relations,
            r => r is { Type: RelationType.Sequel, RelatedExternalId: "4002" });

        Assert.Contains(
            Title(result, "4002").Relations,
            r => r is { Type: RelationType.Prequel, RelatedExternalId: "4001" });
    }

    [Fact]
    public void A_complete_start_date_is_read()
    {
        Assert.Equal(new DateOnly(1996, 4, 5), Title(ParseFixture(), "4002").StartDate);
    }

    [Theory]
    [InlineData("4003")] // year only
    [InlineData("4021")] // no startDate at all
    public void A_partial_or_absent_start_date_is_null_rather_than_guessed(string externalId)
    {
        // Filling a missing month in as January would invent a fact and produce a
        // wrong order — a series that aired in October sorting ahead of one that
        // aired in March. An unknown date sorting last is honest; a guessed one is
        // not.
        Assert.Null(Title(ParseFixture(), externalId).StartDate);
    }

    [Fact]
    public void A_colour_is_lower_cased_so_stored_values_match_each_other()
    {
        Assert.Equal("#e4a15d", Title(ParseFixture(), "4001").CoverImageColor);
    }

    [Theory]
    [InlineData("4003")] // not a colour
    [InlineData("4020")] // explicitly null
    [InlineData("4021")] // no coverImage object
    public void Anything_that_is_not_a_hex_colour_is_dropped(string externalId)
    {
        // The value ends up in a style attribute, and the field is worthless enough
        // that dropping a malformed one costs nothing.
        Assert.Null(Title(ParseFixture(), externalId).CoverImageColor);
    }

    [Fact]
    public void A_title_with_no_relations_is_read_rather_than_skipped()
    {
        // Roughly half a library is standalone. It has to come back as a title with
        // no edges, because that is what marks it as asked-about and stops it being
        // asked about forever.
        var result = ParseFixture();

        Assert.Empty(Title(result, "4020").Relations);
        Assert.Empty(Title(result, "4021").Relations);
    }

    [Fact]
    public void A_GraphQL_error_arriving_with_HTTP_200_is_a_rejection()
    {
        // The dangerous misreading: treating this as "no relations" would mark every
        // title in the batch as fetched and never ask again — a permanent hole
        // written by a transient failure.
        var result = AniListRelationsParser.Parse(Encoding.UTF8.GetBytes(
            """{"errors":[{"message":"Too Many Requests"}],"data":null}"""));

        Assert.False(result.Succeeded);
        Assert.Contains("Too Many Requests", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(result.Titles);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{"data":{"MediaListCollection":{"lists":[]}}}""")]
    [InlineData("""{"data":null}""")]
    public void A_response_that_is_not_a_relation_page_is_rejected(string json)
    {
        var result = AniListRelationsParser.Parse(Encoding.UTF8.GetBytes(json));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Titles);
    }

    [Fact]
    public void An_empty_page_is_read_rather_than_rejected()
    {
        // Different from a failure: the server answered, and the answer was that it
        // knows none of these ids. Marking them stops the backfill asking forever.
        var result = AniListRelationsParser.Parse(Encoding.UTF8.GetBytes(
            """{"data":{"Page":{"media":[]}}}"""));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Titles);
    }
}

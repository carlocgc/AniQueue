using System.Text;
using AniQueue.Core.Import;

namespace AniQueue.Core.Tests.Import;

/// <summary>
/// The four fields Phase 9b added to the list query: genres, studios, the synopsis
/// and the full-size cover (D49).
///
/// They are tested here rather than against a database because everything worth
/// checking about them is a reading decision — what an absent list means, which of
/// two flags belongs to the edge and which to the node, and whether the synopsis
/// comes back the way AniList wrote it.
/// </summary>
public class AniListEnrichmentParsingTests
{
    private static readonly AniListJsonParser Parser = new();

    private static Stream Json(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    /// <summary>One entry, with whatever media fields the test is about spliced in.</summary>
    private static Stream Response(string mediaFields) => Json(
        $$"""
        {
          "data": {
            "MediaListCollection": {
              "hasNextChunk": false,
              "lists": [{
                "name": "Planning",
                "isCustomList": false,
                "entries": [{
                  "status": "PLANNING",
                  "progress": 0,
                  "media": {
                    "id": 16498,
                    "type": "ANIME",
                    "format": "TV",
                    "title": { "romaji": "Shingeki no Kyojin" },
                    {{mediaFields}}
                  }
                }]
              }]
            }
          }
        }
        """);

    [Fact]
    public async Task Genres_arrive_in_the_order_the_source_lists_them()
    {
        await using var stream = Response("""
            "genres": ["Action", "Drama", "Fantasy"]
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.Equal(["Action", "Drama", "Fantasy"], result.Entries[0].Genres);
    }

    [Fact]
    public async Task A_response_carrying_no_genres_says_nothing_rather_than_saying_none()
    {
        // The distinction the whole merge rests on. An empty list here has to be
        // indistinguishable from an absent one, because both mean "this source did
        // not tell us" — and the moment one of them means "this title has no genres",
        // a MyAnimeList re-import starts erasing them (D49).
        await using var absent = Response("\"seasonYear\": 2013");
        await using var empty = Response("\"genres\": []");

        Assert.Empty((await Parser.ParseAsync(absent)).Entries[0].Genres);
        Assert.Empty((await Parser.ParseAsync(empty)).Entries[0].Genres);
    }

    [Fact]
    public async Task A_genre_listed_twice_is_carried_once()
    {
        // The join is keyed on the pair, so a duplicate would abort a whole sync on a
        // primary key violation rather than costing one wasted row.
        await using var stream = Response("""
            "genres": ["Action", "action", "Drama"]
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.Equal(["Action", "Drama"], result.Entries[0].Genres);
    }

    [Fact]
    public async Task The_main_studio_is_told_apart_from_the_companies_that_funded_it()
    {
        await using var stream = Response("""
            "studios": { "edges": [
              { "isMain": true, "node": { "name": "Wit Studio", "isAnimationStudio": true } },
              { "isMain": false, "node": { "name": "Production I.G", "isAnimationStudio": true } },
              { "isMain": false, "node": { "name": "Pony Canyon", "isAnimationStudio": false } }
            ] }
            """);

        var result = await Parser.ParseAsync(stream);
        var studios = result.Entries[0].Studios;

        Assert.Equal(3, studios.Count);
        Assert.Equal(new ParsedStudio("Wit Studio", IsMain: true, IsAnimationStudio: true), studios[0]);
        Assert.Equal(new ParsedStudio("Pony Canyon", IsMain: false, IsAnimationStudio: false), studios[2]);
    }

    [Fact]
    public async Task A_title_with_no_main_studio_flagged_yields_none()
    {
        // Real and reasonably common. The dialog shows no studio line rather than
        // promoting whichever company happened to come back first (D25, D49).
        await using var stream = Response("""
            "studios": { "edges": [
              { "isMain": false, "node": { "name": "Pony Canyon", "isAnimationStudio": false } }
            ] }
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.DoesNotContain(result.Entries[0].Studios, s => s.IsMain);
    }

    [Fact]
    public async Task A_studio_credited_twice_keeps_the_stronger_claim()
    {
        await using var stream = Response("""
            "studios": { "edges": [
              { "isMain": false, "node": { "name": "Wit Studio", "isAnimationStudio": true } },
              { "isMain": true, "node": { "name": "Wit Studio", "isAnimationStudio": true } }
            ] }
            """);

        var result = await Parser.ParseAsync(stream);

        var studio = Assert.Single(result.Entries[0].Studios);
        Assert.True(studio.IsMain);
    }

    [Fact]
    public async Task A_missing_is_main_flag_is_read_as_false_rather_than_failing_the_entry()
    {
        await using var stream = Response("""
            "studios": { "edges": [ { "node": { "name": "Wit Studio" } } ] }
            """);

        var result = await Parser.ParseAsync(stream);

        var studio = Assert.Single(result.Entries[0].Studios);
        Assert.False(studio.IsMain);
        Assert.False(studio.IsAnimationStudio);
    }

    [Fact]
    public async Task The_synopsis_comes_back_exactly_as_AniList_wrote_it()
    {
        // Spoiler markup intact, and the line breaks AniList's users write as HTML
        // still there. Both are the renderer's problem by design: masking a spoiler
        // is only possible while ~!...!~ is still a delimiter rather than markup, and
        // storing a transformation instead would need a refetch to undo (D49).
        const string Synopsis = "Humanity fights back.<br>~!Eren becomes the villain.!~";

        await using var stream = Response($"""
            "description": "{Synopsis}"
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.Equal(Synopsis, result.Entries[0].Description);
    }

    [Fact]
    public async Task Both_cover_sizes_are_read_from_the_one_response()
    {
        await using var stream = Response("""
            "coverImage": {
              "medium": "https://s4.anilist.co/medium/bx16498.jpg",
              "extraLarge": "https://s4.anilist.co/large/bx16498.jpg"
            }
            """);

        var entry = (await Parser.ParseAsync(stream)).Entries[0];

        Assert.Equal("https://s4.anilist.co/medium/bx16498.jpg", entry.CoverImageUrl);
        Assert.Equal("https://s4.anilist.co/large/bx16498.jpg", entry.CoverImageFullUrl);
    }

    [Fact]
    public async Task A_response_carrying_only_the_thumbnail_leaves_the_full_size_cover_unclaimed()
    {
        // What every AniList response looked like before Phase 9b. The full-size row
        // must simply not be created, rather than being created pointing at the
        // thumbnail — which would cache the same 100px picture twice and put it
        // behind a dialog expecting 460px.
        await using var stream = Response("""
            "coverImage": { "medium": "https://s4.anilist.co/medium/bx16498.jpg" }
            """);

        var entry = (await Parser.ParseAsync(stream)).Entries[0];

        Assert.NotNull(entry.CoverImageUrl);
        Assert.Null(entry.CoverImageFullUrl);
    }

    [Fact]
    public async Task A_cover_address_that_is_not_http_is_refused_at_either_size()
    {
        await using var stream = Response("""
            "coverImage": {
              "medium": "javascript:alert(1)",
              "extraLarge": "file:///etc/passwd"
            }
            """);

        var entry = (await Parser.ParseAsync(stream)).Entries[0];

        Assert.Null(entry.CoverImageUrl);
        Assert.Null(entry.CoverImageFullUrl);
    }
}

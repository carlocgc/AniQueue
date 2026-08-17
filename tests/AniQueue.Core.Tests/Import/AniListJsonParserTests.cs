using System.Reflection;
using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;

namespace AniQueue.Core.Tests.Import;

/// <summary>
/// AniList's vocabulary, tested against a committed fixture so no test touches the
/// network (§8).
///
/// The cases here are chosen from what the live probe could <i>not</i> confirm. The
/// real library used to verify the API contained only COMPLETED, CURRENT and
/// PLANNING entries, no partial FuzzyDate and no custom list — so the mappings most
/// likely to be wrong are exactly the ones a captured response would have left
/// untested.
/// </summary>
public class AniListJsonParserTests
{
    private static readonly AniListJsonParser Parser = new();

    private static Stream Fixture()
    {
        var stream = typeof(AniListJsonParserTests).Assembly.GetManifestResourceStream(
            "AniQueue.Core.Tests.Import.Fixtures.anilist-medialistcollection.json");

        return stream ?? throw new InvalidOperationException(
            "The AniList fixture is missing from the test assembly.");
    }

    private static Stream Json(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static async Task<ParseResult> ParseFixtureAsync(
        TitleLanguage preferred = TitleLanguage.Romaji)
    {
        await using var stream = Fixture();
        return await Parser.ParseAsync(stream, preferred);
    }

    private static ParsedLibraryEntry Entry(ParseResult result, string aniListId) =>
        result.Entries.Single(e =>
            e.ExternalIds.Any(id => id.Source == AnimeSource.AniList && id.Value == aniListId));

    [Fact]
    public async Task The_fixture_parses_into_one_entry_per_distinct_title()
    {
        var result = await ParseFixtureAsync();

        Assert.False(result.IsFileRejected);
        Assert.Equal(10, result.Entries.Count);
    }

    [Fact]
    public async Task A_title_filed_in_a_custom_list_as_well_is_read_once()
    {
        // AniList lets one entry sit in its status list and any number of custom
        // lists, and whether that surfaces it twice in the collection is unverified —
        // the probed account had no custom lists. Trusting the collection to be flat
        // would give the user a duplicate row per favourited title.
        var result = await ParseFixtureAsync();

        Assert.Single(
            result.Entries,
            e => e.ExternalIds.Any(id => id.Source == AnimeSource.AniList && id.Value == "900101"));

        Assert.Contains(result.Problems, p =>
            p.Message.Contains("more than one", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("900101", 9)]  // 87
    [InlineData("900106", 10)] // 100
    [InlineData("900107", 6)]  // 55, which rounds up rather than to even
    [InlineData("900108", 2)]  // 23
    public async Task A_hundred_point_score_converts_to_AniQueues_ten(string id, int expected)
    {
        // Requesting POINT_100 leaves this last step ours, which is the point: the
        // scale is the finest-grained integer one AniList offers, so every native
        // format converts onto it without loss and the division happens where it can
        // be tested.
        var result = await ParseFixtureAsync();

        Assert.Equal(expected, Entry(result, id).UserScore);
    }

    [Fact]
    public async Task A_score_of_eighty_five_rounds_up_rather_than_to_even()
    {
        // .NET's default Math.Round is banker's rounding, which would send 8.5 down
        // to 8. Away-from-zero is specified deliberately, and this is the case that
        // catches anyone removing the argument as noise.
        var result = await ParseFixtureAsync();

        Assert.Equal(9, Entry(result, "900102").UserScore);
    }

    [Fact]
    public async Task A_score_below_five_clamps_to_one_rather_than_vanishing()
    {
        // 4/100 divides to 0.4 and rounds to 0, which is indistinguishable from
        // unscored. A 1 separates a disliked show from an unrated one, and that
        // distinction is what Phase 9 ranks on.
        var result = await ParseFixtureAsync();

        Assert.Equal(1, Entry(result, "900103").UserScore);
    }

    [Fact]
    public async Task A_score_of_zero_is_unscored_rather_than_a_rating()
    {
        // A quarter of the measured library is unscored, so this is not an edge
        // case — and a 0 reaching the database violates CK_LibraryEntries_UserScoreRange
        // mid-transaction.
        var result = await ParseFixtureAsync();

        Assert.Null(Entry(result, "900104").UserScore);
    }

    [Theory]
    [InlineData("900101", LibraryStatus.Completed)]
    [InlineData("900105", LibraryStatus.Watching)]
    [InlineData("900107", LibraryStatus.OnHold)]
    [InlineData("900108", LibraryStatus.Dropped)]
    [InlineData("900109", LibraryStatus.Planning)]
    public async Task Every_AniList_status_maps_onto_a_library_status(string id, LibraryStatus expected)
    {
        var result = await ParseFixtureAsync();

        Assert.Equal(expected, Entry(result, id).Status);
    }

    [Fact]
    public async Task A_repeating_entry_is_watching_not_planning()
    {
        // Someone five episodes into a re-watch is watching the show, whatever their
        // intent was when they started (D12, D15). Reading REPEATING as Planning
        // would put a half-watched title back into the backlog as queueable.
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900106");

        Assert.Equal(LibraryStatus.Watching, entry.Status);
        Assert.Equal(5, entry.EpisodesWatched);
        Assert.Equal(2, entry.TimesRewatched);
    }

    [Fact]
    public async Task A_complete_fuzzy_date_becomes_a_date()
    {
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900101");

        Assert.Equal(new DateOnly(2021, 1, 12), entry.DateStarted);
        Assert.Equal(new DateOnly(2021, 6, 30), entry.DateCompleted);
    }

    [Fact]
    public async Task A_partial_fuzzy_date_is_null_rather_than_invented()
    {
        // A year with no month or day is real information with nowhere truthful to
        // go: DateOnly would have to invent 1 January, and the user would see a date
        // they never stated rendered as though they had.
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900102");

        Assert.Null(entry.DateStarted);
        Assert.Null(entry.DateCompleted);
    }

    [Fact]
    public async Task An_impossible_fuzzy_date_is_null_rather_than_an_exception()
    {
        // The 31st of February. The three components are independently nullable and
        // independently wrong, and one bad date must not cost the whole fetch.
        var result = await ParseFixtureAsync();

        Assert.Null(Entry(result, "900110").DateStarted);
    }

    [Fact]
    public async Task A_missing_english_title_falls_back_rather_than_writing_null()
    {
        // English is absent for roughly one title in seven. Title is a required
        // column, so a preference without a fallback would push null into it for
        // every one of them (D22).
        var result = await ParseFixtureAsync(TitleLanguage.English);
        var entry = Entry(result, "900102");

        Assert.Equal("Yoake Cafe", entry.Title);
        Assert.Equal("夜明けカフェ", entry.AlternativeTitle);
    }

    [Theory]
    [InlineData(TitleLanguage.Romaji, "Sora no Kakera", "Fragments of Sky")]
    [InlineData(TitleLanguage.English, "Fragments of Sky", "Sora no Kakera")]
    [InlineData(TitleLanguage.Native, "空の欠片", "Sora no Kakera")]
    public async Task The_preferred_variant_is_the_title_and_another_is_kept_beside_it(
        TitleLanguage preferred,
        string expectedTitle,
        string expectedAlternative)
    {
        var result = await ParseFixtureAsync(preferred);
        var entry = Entry(result, "900101");

        Assert.Equal(expectedTitle, entry.Title);
        Assert.Equal(expectedAlternative, entry.AlternativeTitle);
    }

    [Fact]
    public async Task An_entry_supplies_both_its_own_id_and_the_MyAnimeList_one()
    {
        // D17's bridge, at its source. Writing both is what lets a sync match a
        // MyAnimeList-imported row rather than duplicate it, whichever service the
        // user started with.
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900101");

        Assert.Equal(
            [
                new ExternalIdentifier(AnimeSource.AniList, "900101"),
                new ExternalIdentifier(AnimeSource.MyAnimeList, "500101")
            ],
            entry.ExternalIds);
    }

    [Fact]
    public async Task A_missing_idMal_leaves_one_identifier_rather_than_a_problem()
    {
        // Six of 753 entries in the measured library carry no idMal. The gap is real
        // and tiny, and an entry that only AniList knows about is perfectly ordinary.
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900102");

        Assert.Equal(
            [new ExternalIdentifier(AnimeSource.AniList, "900102")],
            entry.ExternalIds);
    }

    [Theory]
    [InlineData("900101", MediaType.Tv)]
    [InlineData("900102", MediaType.Tv)]
    [InlineData("900103", MediaType.Movie)]
    [InlineData("900108", MediaType.Ona)]
    [InlineData("900110", MediaType.Special)]
    public async Task Formats_map_onto_media_types(string id, MediaType expected)
    {
        // TV_SHORT included deliberately: a short-form series is still a TV series
        // for every purpose here, and its brevity is carried by the episode duration
        // that the runtime filters actually read.
        var result = await ParseFixtureAsync();

        Assert.Equal(expected, Entry(result, id).MediaType);
    }

    [Fact]
    public async Task Duration_and_release_year_arrive_because_this_is_where_they_come_from()
    {
        // Phase 3's runtime filter, runtime sort and decade chips have been inert in
        // every real installation, because nothing populated these two columns.
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900101");

        Assert.Equal(24, entry.EpisodeDurationMinutes);
        Assert.Equal(2021, entry.ReleaseYear);
        Assert.Equal("https://cdn.example.invalid/cover/bx900101.jpg", entry.CoverImageUrl);
    }

    [Fact]
    public async Task A_missing_season_year_is_null_rather_than_zero()
    {
        var result = await ParseFixtureAsync();

        Assert.Null(Entry(result, "900103").ReleaseYear);
    }

    [Fact]
    public async Task An_ongoing_series_has_no_episode_count()
    {
        var result = await ParseFixtureAsync();
        var entry = Entry(result, "900105");

        Assert.Null(entry.EpisodeCount);
        Assert.Equal(7, entry.EpisodesWatched);
    }

    [Fact]
    public async Task A_cover_url_that_is_not_http_is_dropped()
    {
        // The value is a remote string destined for an img src. Nothing about a
        // response body is trustworthy enough to skip checking the scheme.
        var result = await ParseFixtureAsync();

        Assert.Null(Entry(result, "900109").CoverImageUrl);
    }

    [Fact]
    public async Task A_GraphQL_errors_array_is_a_rejection_rather_than_an_empty_list()
    {
        // This is the dangerous one. GraphQL reports failure inside an HTTP 200, and
        // reading that body as zero entries is indistinguishable from the user having
        // deleted their entire list — which is precisely what D19's absence handling
        // would act on.
        await using var stream = Json(
            """
            {
              "errors": [{ "message": "User not found", "status": 404 }],
              "data": { "MediaListCollection": null }
            }
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.True(result.IsFileRejected);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Message.Contains("User not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_truncated_response_is_a_rejection()
    {
        await using var stream = Json("""{ "data": { "MediaListCollection": { "lis""");

        var result = await Parser.ParseAsync(stream);

        Assert.True(result.IsFileRejected);
    }

    [Fact]
    public async Task A_body_that_is_not_a_list_response_is_a_rejection()
    {
        await using var stream = Json("""{ "data": { "Viewer": { "id": 1 } } }""");

        var result = await Parser.ParseAsync(stream);

        Assert.True(result.IsFileRejected);
    }

    [Fact]
    public async Task An_empty_list_is_not_a_rejection()
    {
        // An account with nothing on its list is a real account. The preview showing
        // nothing to do is the honest answer, and it is only safe because every
        // *failed* fetch above rejects instead of arriving here.
        await using var stream = Json("""{ "data": { "MediaListCollection": { "lists": [] } } }""");

        var result = await Parser.ParseAsync(stream);

        Assert.False(result.IsFileRejected);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Message.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_non_anime_media_record_is_skipped()
    {
        // The query pins type: ANIME so this should never arrive. The guard is cheap
        // and the alternative is manga entering an anime backlog.
        await using var stream = Json(
            """
            {
              "data": { "MediaListCollection": { "lists": [{ "entries": [
                { "status": "COMPLETED", "media": {
                    "id": 1, "type": "MANGA", "title": { "romaji": "Something" } } }
              ] }] } }
            }
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Message.Contains("not an anime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_with_no_title_in_any_language_is_skipped_not_thrown()
    {
        await using var stream = Json(
            """
            {
              "data": { "MediaListCollection": { "lists": [{ "entries": [
                { "status": "PLANNING", "media": {
                    "id": 2, "type": "ANIME",
                    "title": { "romaji": null, "english": null, "native": null } } }
              ] }] } }
            }
            """);

        var result = await Parser.ParseAsync(stream);

        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Message.Contains("no title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watching_more_episodes_than_exist_drops_the_episode_count_not_the_progress()
    {
        // The count is the user's own record; the total is catalogue metadata. The
        // MyAnimeList parser resolves the same contradiction the same way.
        await using var stream = Json(
            """
            {
              "data": { "MediaListCollection": { "lists": [{ "entries": [
                { "status": "CURRENT", "progress": 30, "media": {
                    "id": 3, "type": "ANIME", "episodes": 12,
                    "title": { "romaji": "Overrun" } } }
              ] }] } }
            }
            """);

        var result = await Parser.ParseAsync(stream);
        var entry = Assert.Single(result.Entries);

        Assert.Equal(30, entry.EpisodesWatched);
        Assert.Null(entry.EpisodeCount);
        Assert.Contains(result.Problems, p => p.Message.Contains("episode count was ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_oversized_response_is_rejected_before_it_is_parsed()
    {
        // A remote body is not the user's file, and Content-Length is supplied by the
        // other end. The ceiling is applied while reading rather than trusted.
        var parser = new AniListJsonParser(new ImportLimits { MaxBytes = 64 });

        await using var stream = Json(new string('x', 512));

        var result = await parser.ParseAsync(stream);

        Assert.True(result.IsFileRejected);
    }

    [Fact]
    public void The_format_name_is_shown_to_the_user()
    {
        Assert.Equal("AniList", Parser.FormatName);
    }
}

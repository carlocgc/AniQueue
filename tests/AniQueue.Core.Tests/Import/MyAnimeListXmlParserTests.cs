using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;

namespace AniQueue.Core.Tests.Import;

public class MyAnimeListXmlParserTests
{
    private static readonly MyAnimeListXmlParser Parser = new();

    private static Stream Xml(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static string Export(params string[] animeElements) =>
        $"""
         <?xml version="1.0" encoding="UTF-8" ?>
         <myanimelist>
           <myinfo><user_name>tester</user_name></myinfo>
           {string.Join("\n  ", animeElements)}
         </myanimelist>
         """;

    private const string GoldenBoy =
        """
        <anime>
          <series_animedb_id>268</series_animedb_id>
          <series_title><![CDATA[Golden Boy]]></series_title>
          <series_type>OVA</series_type>
          <series_episodes>6</series_episodes>
          <my_watched_episodes>6</my_watched_episodes>
          <my_start_date>2024-01-05</my_start_date>
          <my_finish_date>2024-01-09</my_finish_date>
          <my_score>9</my_score>
          <my_status>Completed</my_status>
          <my_times_watched>1</my_times_watched>
        </anime>
        """;

    [Fact]
    public async Task Parses_a_complete_entry()
    {
        var result = await Parser.ParseAsync(Xml(Export(GoldenBoy)));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(AnimeSource.MyAnimeList, entry.Source);
        Assert.Equal("268", entry.SourceAnimeId);
        Assert.Equal("Golden Boy", entry.Title);          // CDATA unwrapped
        Assert.Equal(MediaType.Ova, entry.MediaType);
        Assert.Equal(6, entry.EpisodeCount);
        Assert.Equal(6, entry.EpisodesWatched);
        Assert.Equal(9, entry.UserScore);
        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(new DateOnly(2024, 1, 5), entry.DateStarted);
        Assert.Equal(new DateOnly(2024, 1, 9), entry.DateCompleted);
        Assert.Equal(1, entry.TimesRewatched);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData("Completed", LibraryStatus.Completed)]
    [InlineData("Watching", LibraryStatus.Watching)]
    [InlineData("On-Hold", LibraryStatus.OnHold)]
    [InlineData("On Hold", LibraryStatus.OnHold)]
    [InlineData("Dropped", LibraryStatus.Dropped)]
    [InlineData("Plan to Watch", LibraryStatus.Planning)]
    [InlineData("plantowatch", LibraryStatus.Planning)]
    public async Task Maps_status_across_export_spelling_variants(string raw, LibraryStatus expected)
    {
        var result = await Parser.ParseAsync(Xml(Export(
            $"<anime><series_title>T</series_title><my_status>{raw}</my_status></anime>")));

        Assert.Equal(expected, Assert.Single(result.Entries).Status);
    }

    [Fact]
    public async Task An_unrecognised_status_falls_back_to_planning_and_is_reported()
    {
        // Better to import the title into the backlog and say so than to drop it.
        var result = await Parser.ParseAsync(Xml(Export(
            "<anime><series_title>Odd</series_title><my_status>Rewatching</my_status></anime>")));

        Assert.Equal(LibraryStatus.Planning, Assert.Single(result.Entries).Status);
        Assert.Contains(result.Problems, p => p.Message.Contains("Rewatching", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TV", MediaType.Tv)]
    [InlineData("Movie", MediaType.Movie)]
    [InlineData("OVA", MediaType.Ova)]
    [InlineData("ONA", MediaType.Ona)]
    [InlineData("Special", MediaType.Special)]
    [InlineData("Music", MediaType.Music)]
    [InlineData("", MediaType.Unknown)]
    [InlineData("Nonsense", MediaType.Unknown)]
    public async Task Maps_media_type(string raw, MediaType expected)
    {
        var result = await Parser.ParseAsync(Xml(Export(
            $"<anime><series_title>T</series_title><series_type>{raw}</series_type></anime>")));

        Assert.Equal(expected, Assert.Single(result.Entries).MediaType);
    }

    [Fact]
    public async Task Treats_the_zero_date_as_no_date()
    {
        // MAL writes 0000-00-00 for "not set". It is not a representable date.
        var result = await Parser.ParseAsync(Xml(Export(
            """
            <anime>
              <series_title>Unstarted</series_title>
              <my_start_date>0000-00-00</my_start_date>
              <my_finish_date>0000-00-00</my_finish_date>
            </anime>
            """)));

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.DateStarted);
        Assert.Null(entry.DateCompleted);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2024-13-45")]
    [InlineData("")]
    public async Task Treats_an_unparseable_date_as_no_date(string raw)
    {
        var result = await Parser.ParseAsync(Xml(Export(
            $"<anime><series_title>T</series_title><my_start_date>{raw}</my_start_date></anime>")));

        Assert.Null(Assert.Single(result.Entries).DateStarted);
    }

    [Fact]
    public async Task A_score_of_zero_means_unscored_not_a_rating_of_zero()
    {
        // Importing this as 0/10 would both violate the database constraint and
        // poison every recommendation derived from the user's taste.
        var result = await Parser.ParseAsync(Xml(Export(
            "<anime><series_title>T</series_title><my_score>0</my_score></anime>")));

        Assert.Null(Assert.Single(result.Entries).UserScore);
    }

    [Theory]
    [InlineData("11")]
    [InlineData("-3")]
    [InlineData("99")]
    public async Task An_out_of_range_score_is_discarded_and_reported(string raw)
    {
        var result = await Parser.ParseAsync(Xml(Export(
            $"<anime><series_title>T</series_title><my_score>{raw}</my_score></anime>")));

        Assert.Null(Assert.Single(result.Entries).UserScore);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task An_episode_count_of_zero_means_unknown()
    {
        var result = await Parser.ParseAsync(Xml(Export(
            "<anime><series_title>Ongoing</series_title><series_episodes>0</series_episodes></anime>")));

        Assert.Null(Assert.Single(result.Entries).EpisodeCount);
    }

    [Fact]
    public async Task Watching_more_episodes_than_exist_keeps_the_watch_count_and_drops_the_total()
    {
        // The watch count is the user's own record; the total is catalogue
        // metadata. When they contradict each other, trust the user.
        var result = await Parser.ParseAsync(Xml(Export(
            """
            <anime>
              <series_title>Contradictory</series_title>
              <series_episodes>12</series_episodes>
              <my_watched_episodes>24</my_watched_episodes>
            </anime>
            """)));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(24, entry.EpisodesWatched);
        Assert.Null(entry.EpisodeCount);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task An_entry_without_a_title_is_skipped_but_the_rest_import()
    {
        // One broken record must not cost the user the whole export.
        var result = await Parser.ParseAsync(Xml(Export(
            "<anime><series_animedb_id>1</series_animedb_id></anime>",
            GoldenBoy)));

        Assert.Equal("Golden Boy", Assert.Single(result.Entries).Title);
        Assert.Contains(result.Problems, p => p.RecordNumber == 1);
    }

    [Fact]
    public async Task Malformed_xml_is_reported_rather_than_thrown()
    {
        var result = await Parser.ParseAsync(Xml("<myanimelist><anime><series_title>Truncated"));

        Assert.True(result.IsFileRejected);
        Assert.Empty(result.Entries);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task A_file_with_no_anime_entries_is_reported()
    {
        var result = await Parser.ParseAsync(Xml("<myanimelist><myinfo /></myanimelist>"));

        Assert.Empty(result.Entries);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task External_entities_are_not_resolved()
    {
        // XXE: without DtdProcessing.Prohibit this would read a local file and
        // place its contents into the title.
        var hostile =
            """
            <?xml version="1.0"?>
            <!DOCTYPE root [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <myanimelist><anime><series_title>&xxe;</series_title></anime></myanimelist>
            """;

        var result = await Parser.ParseAsync(Xml(hostile));

        Assert.True(result.IsFileRejected);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Entity_expansion_attacks_are_refused()
    {
        // "Billion laughs" — nested entities that expand exponentially. Prohibiting
        // DTDs stops it before any expansion occurs.
        var hostile =
            """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
            ]>
            <myanimelist><anime><series_title>&lol3;</series_title></anime></myanimelist>
            """;

        var result = await Parser.ParseAsync(Xml(hostile));

        Assert.True(result.IsFileRejected);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused_without_parsing()
    {
        var parser = new MyAnimeListXmlParser(new ImportLimits { MaxBytes = 512 });
        var padding = new string('x', 4096);

        var result = await parser.ParseAsync(Xml(Export(
            $"<anime><series_title>{padding}</series_title></anime>")));

        Assert.True(result.IsFileRejected);
        Assert.Contains(result.Problems, p => p.Message.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Entries_beyond_the_record_limit_are_dropped_and_reported()
    {
        var parser = new MyAnimeListXmlParser(new ImportLimits { MaxEntries = 2 });
        var many = Enumerable.Range(1, 10)
            .Select(i => $"<anime><series_title>Title {i}</series_title></anime>")
            .ToArray();

        var result = await parser.ParseAsync(Xml(Export(many)));

        Assert.Equal(2, result.Entries.Count);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task Parsing_the_same_export_twice_yields_identical_results()
    {
        // Determinism is what makes the preview trustworthy: the user confirms a
        // preview, and the commit must act on exactly the same interpretation.
        var first = await Parser.ParseAsync(Xml(Export(GoldenBoy)));
        var second = await Parser.ParseAsync(Xml(Export(GoldenBoy)));

        Assert.Equal(first.Entries, second.Entries);
    }
}

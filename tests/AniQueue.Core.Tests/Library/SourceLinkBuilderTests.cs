using AniQueue.Core.Domain;
using AniQueue.Core.Library;

namespace AniQueue.Core.Tests.Library;

public class SourceLinkBuilderTests
{
    [Fact]
    public void Builds_a_MyAnimeList_link()
    {
        var link = SourceLinkBuilder.For(AnimeSource.MyAnimeList, "268");

        Assert.NotNull(link);
        Assert.Equal("https://myanimelist.net/anime/268", link.Url);
        Assert.Equal("MAL", link.ShortName);
        Assert.Equal("MyAnimeList", link.SiteName);
    }

    [Fact]
    public void Builds_an_AniList_link()
    {
        var link = SourceLinkBuilder.For(AnimeSource.AniList, "21");

        Assert.NotNull(link);
        Assert.Equal("https://anilist.co/anime/21", link.Url);
        Assert.Equal("AniList", link.ShortName);
    }

    [Fact]
    public void The_accessible_name_spells_the_site_out_and_names_the_title()
    {
        // The badge shows "MAL", which is jargon on its own. A screen reader
        // announcing just that would tell the user nothing about where the link
        // goes or which row it belongs to.
        var link = SourceLinkBuilder.For(AnimeSource.MyAnimeList, "268");

        Assert.NotNull(link);
        Assert.Equal("Open Golden Boy on MyAnimeList", link.DescribeFor("Golden Boy"));
    }

    [Fact]
    public void Manual_entries_have_nowhere_to_link_to()
    {
        // They were typed in here; there is no external page.
        Assert.Null(SourceLinkBuilder.For(AnimeSource.Manual, null));
        Assert.Null(SourceLinkBuilder.For(AnimeSource.Manual, "268"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_identifier_means_no_link(string? id) =>
        Assert.Null(SourceLinkBuilder.For(AnimeSource.MyAnimeList, id));

    [Theory]
    [InlineData("../../evil")]
    [InlineData("268/../../admin")]
    [InlineData("javascript:alert(1)")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("12 34")]
    public void A_non_numeric_identifier_produces_no_link(string id)
    {
        // Identifiers arrive in imported files and are not trusted. Refusing
        // anything that is not a plain number means nothing has to be escaped
        // downstream, and a hand-edited export cannot steer the link anywhere.
        Assert.Null(SourceLinkBuilder.For(AnimeSource.MyAnimeList, id));
    }

    private static readonly string[] ExpectedHosts = ["myanimelist.net", "anilist.co"];

    private static readonly AnimeSource[] LinkableSources =
        [AnimeSource.MyAnimeList, AnimeSource.AniList];

    [Fact]
    public void Every_produced_link_is_https_to_the_expected_host()
    {
        foreach (var source in LinkableSources)
        {
            var link = SourceLinkBuilder.For(source, "1");

            Assert.NotNull(link);
            var uri = new Uri(link.Url);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(uri.Host, ExpectedHosts);
        }
    }
}

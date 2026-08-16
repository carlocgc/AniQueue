using AniQueue.Core.Domain;
using AniQueue.Core.Library;

namespace AniQueue.Core.Tests.Library;

public class SourceLinkBuilderTests
{
    [Fact]
    public void Builds_a_MyAnimeList_link()
    {
        var link = SourceLinkBuilder.ForAnime(AnimeSource.MyAnimeList, "268");

        Assert.NotNull(link);
        Assert.Equal("https://myanimelist.net/anime/268", link.Url);
        Assert.Equal("View on MyAnimeList", link.Label);
    }

    [Fact]
    public void Builds_an_AniList_link()
    {
        var link = SourceLinkBuilder.ForAnime(AnimeSource.AniList, "21");

        Assert.NotNull(link);
        Assert.Equal("https://anilist.co/anime/21", link.Url);
    }

    [Fact]
    public void Manual_entries_have_nowhere_to_link_to()
    {
        // They were typed in here; there is no external page.
        Assert.Null(SourceLinkBuilder.ForAnime(AnimeSource.Manual, null));
        Assert.Null(SourceLinkBuilder.ForAnime(AnimeSource.Manual, "268"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_identifier_means_no_link(string? id) =>
        Assert.Null(SourceLinkBuilder.ForAnime(AnimeSource.MyAnimeList, id));

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
        Assert.Null(SourceLinkBuilder.ForAnime(AnimeSource.MyAnimeList, id));
    }

    [Fact]
    public void Every_produced_link_is_https_to_the_expected_host()
    {
        foreach (var source in new[] { AnimeSource.MyAnimeList, AnimeSource.AniList })
        {
            var link = SourceLinkBuilder.ForAnime(source, "1");

            Assert.NotNull(link);
            var uri = new Uri(link.Url);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(uri.Host, new[] { "myanimelist.net", "anilist.co" });
        }
    }
}

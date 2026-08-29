using AniQueue.Core.Domain;
using AniQueue.Core.Library;

namespace AniQueue.Core.Tests.Library;

/// <summary>
/// Which rendition a list row asks for, and what it does while that one is missing.
/// </summary>
/// <remarks>
/// The thumbnail is the right rendition for a list of fifty
/// rows of a wide table, each showing a 40px sliver. The card makes the picture leading
/// element of a card and a 64px slot wants more than a 100px image, so the row asks
/// for the full rendition and falls back the way the dialog does.
///
/// Asserted on the URL rather than on a flag, because the rendition is only ever
/// visible as a segment of the address the endpoint serves.
/// </remarks>
public sealed class ListRowArtTests
{
    private static LibraryListItem Row(
        string? posterHash, string? thumbnailHash, string? colour = null) => new()
    {
        AnimeId = 7,
        Title = "A title",
        Status = LibraryStatus.Planning,
        PosterContentHash = posterHash,
        PosterFileExtension = posterHash is null ? null : "jpg",
        CoverContentHash = thumbnailHash,
        CoverFileExtension = thumbnailHash is null ? null : "jpg",
        CoverImageColor = colour
    };

    [Fact]
    public void The_full_rendition_is_used_where_it_has_been_fetched()
    {
        var cover = Row(posterHash: "aaa", thumbnailHash: "bbb").Cover;

        Assert.Contains("aaa", cover.Url);
        Assert.DoesNotContain("bbb", cover.Url);
    }

    /// <summary>
    /// The step that matters in practice: a fresh install has thumbnails for minutes
    /// before it has posters, and a page of colour blocks during that window would
    /// look broken rather than pending.
    /// </summary>
    [Fact]
    public void The_thumbnail_stands_in_until_the_full_one_arrives()
    {
        var cover = Row(posterHash: null, thumbnailHash: "bbb").Cover;

        Assert.Contains("bbb", cover.Url);
    }

    [Fact]
    public void With_neither_it_falls_to_the_colour_the_source_published()
    {
        var cover = Row(posterHash: null, thumbnailHash: null, colour: "#50bbf1").Cover;

        Assert.Null(cover.Url);
        Assert.Equal("#50bbf1", cover.Colour);
    }
}

using AniQueue.Core.Artwork;

namespace AniQueue.Core.Tests.Artwork;

/// <summary>
/// What a row renders for its art. Where the picture actually lives is
/// <c>ArtworkPaths</c>' business and is tested there.
/// </summary>
public class CoverImageResolverTests
{
    [Fact]
    public void A_cached_picture_resolves_to_an_address_carrying_its_hash()
    {
        var cover = CoverImageResolver.ForAnime(16498, "abc123", ".jpg", "#50bbf1");

        Assert.Equal("/art/thumbnails/16498/abc123.jpg", cover.Url);
        Assert.Equal("#50bbf1", cover.Colour);
        Assert.True(cover.HasImage);
    }

    [Fact]
    public void A_title_with_no_cached_picture_falls_back_to_its_colour()
    {
        var cover = CoverImageResolver.ForAnime(16498, contentHash: null, fileExtension: null, "#50bbf1");

        Assert.Null(cover.Url);
        Assert.Equal("#50bbf1", cover.Colour);
        Assert.False(cover.IsEmpty);
    }

    [Fact]
    public void A_title_with_neither_resolves_to_nothing()
    {
        var cover = CoverImageResolver.ForAnime(16498, null, null, null);

        Assert.True(cover.IsEmpty);
    }

    [Fact]
    public void A_hash_without_an_extension_is_not_an_address()
    {
        // Both halves travel in the URL, and one without the other would produce a
        // path the endpoint cannot answer — a broken image rather than a fallback.
        var cover = CoverImageResolver.ForAnime(16498, "abc123", fileExtension: null, colour: null);

        Assert.Null(cover.Url);
    }

    [Theory]
    // Blazor escapes the attribute this lands in, so none of these can break out of
    // it. What escaping cannot stop is a second declaration inside it, and the third
    // case is a third party choosing what a self-hosted page fetches.
    [InlineData("red")]
    [InlineData("#50bbf1; background-image: url(https://example.invalid/track)")]
    [InlineData("url(https://example.invalid/track)")]
    [InlineData("#50bbf")]
    [InlineData("#50bbf1f")]
    [InlineData("#gggggg")]
    [InlineData("50bbf1")]
    [InlineData("")]
    public void A_colour_that_is_not_exactly_six_hexadecimal_digits_is_dropped(string colour)
    {
        var cover = CoverImageResolver.ForAnime(1, null, null, colour);

        Assert.Null(cover.Colour);
        Assert.True(cover.IsEmpty);
    }
}

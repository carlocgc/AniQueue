using AniQueue.Core.Artwork;

namespace AniQueue.Core.Tests.Artwork;

public class CoverImageResolverTests
{
    [Fact]
    public void A_cached_picture_resolves_to_an_address_carrying_its_hash()
    {
        var cover = CoverImageResolver.ForAnime(16498, "abc123", ".jpg", "#50bbf1");

        Assert.Equal("/covers/16498/abc123.jpg", cover.Url);
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

    [Theory]
    [InlineData("abc123.jpg", "abc123", ".jpg")]
    [InlineData("ABCDEF.png", "ABCDEF", ".png")]
    public void A_segment_this_application_could_have_produced_is_parsed(
        string segment, string expectedHash, string expectedExtension)
    {
        Assert.True(CoverImageResolver.TryParseSegment(segment, out var hash, out var extension));
        Assert.Equal(expectedHash, hash);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    // §6 forbids user-supplied file paths, and this is the check that keeps that
    // true: the segment arrives from a request and goes into a filename. It is a
    // whitelist rather than a sanitiser, so every one of these fails for the same
    // dull reason — a separator is not a hexadecimal digit.
    [InlineData("../../etc/passwd")]
    [InlineData("..%2F..%2Fpasswd.jpg")]
    [InlineData("../secrets.jpg")]
    [InlineData("abc/def.jpg")]
    [InlineData("abc\\def.jpg")]
    [InlineData("C:\\windows\\system32.jpg")]
    [InlineData("abc123.jpg\0.txt")]
    [InlineData("abc123")]
    [InlineData(".jpg")]
    [InlineData("abc123.exe")]
    [InlineData("abc123.svg")]
    [InlineData("zzz.jpg")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? segment)
    {
        Assert.False(CoverImageResolver.TryParseSegment(segment, out var hash, out var extension));
        Assert.Null(hash);
        Assert.Null(extension);
    }

    [Fact]
    public void A_hash_longer_than_one_could_ever_be_is_refused()
    {
        // Not a real attack so much as a bound: the filename is built from this, and
        // there is no reason to let a request name a path component of any length.
        Assert.False(CoverImageResolver.TryParseSegment(new string('a', 65) + ".jpg", out _, out _));
    }

    [Fact]
    public void The_served_address_and_the_file_on_disk_are_built_from_the_same_parts()
    {
        // The job writes the file and the endpoint reads it, and they never speak.
        // If these two drifted apart, every picture would cache successfully and
        // none of them would ever be served.
        var cover = CoverImageResolver.ForAnime(42, "deadbeef", ".png", null);

        Assert.NotNull(cover.Url);
        Assert.True(CoverImageResolver.TryParseSegment(
            cover.Url.Split('/')[^1], out var hash, out var extension));
        Assert.Equal("42-deadbeef.png", CoverImageResolver.CacheFileName(42, hash, extension));
    }
}

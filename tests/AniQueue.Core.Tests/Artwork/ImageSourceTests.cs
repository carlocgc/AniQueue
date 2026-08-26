using AniQueue.Core.Artwork;

namespace AniQueue.Core.Tests.Artwork;

public class ImageSourceTests
{
    [Theory]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/small/bx16498-buvcRTBx4NSm.jpg")]
    [InlineData("https://anilist.co/whatever.png")]
    [InlineData("https://S1.ANILIST.CO/file.jpg")]
    public void An_AniList_address_over_https_may_be_fetched(string url) =>
        Assert.True(ImageSource.IsAllowed(url));

    [Theory]
    // The one that matters. A suffix match written as EndsWith and nothing else
    // accepts this, and it is a host the attacker owns.
    [InlineData("https://anilist.co.example.invalid/cover.jpg")]
    [InlineData("https://notanilist.co/cover.jpg")]
    [InlineData("https://example.invalid/cover.jpg")]
    public void A_host_that_merely_ends_in_the_right_letters_may_not(string url) =>
        Assert.False(ImageSource.IsAllowed(url));

    [Theory]
    // Not because a cover is a secret, but because anything on the path could
    // otherwise choose what lands in the cache and gets served from AniQueue's own
    // origin.
    [InlineData("http://s4.anilist.co/cover.jpg")]
    [InlineData("ftp://s4.anilist.co/cover.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:image/png;base64,AAAA")]
    public void Only_https_may(string url) => Assert.False(ImageSource.IsAllowed(url));

    [Theory]
    [InlineData("/cover.jpg")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_an_absolute_address_may_not(string? url) =>
        Assert.False(ImageSource.IsAllowed(url));

    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("image/webp", ".webp")]
    // A header is allowed to carry parameters and casing, and a server that sends
    // them is not sending a different type.
    [InlineData("IMAGE/JPEG; charset=binary", ".jpg")]
    [InlineData("  image/png  ", ".png")]
    public void A_picture_is_recognised_by_what_the_server_said_it_sent(string contentType, string expected) =>
        Assert.Equal(expected, ImageSource.ExtensionFor(contentType));

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    // The interesting one: a URL ending in .jpg that serves something else. The
    // path is the part a third party controls, so it never gets a vote.
    [InlineData("image/svg+xml")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_picture(string? contentType) =>
        Assert.Null(ImageSource.ExtensionFor(contentType));

    [Fact]
    public void Every_extension_it_produces_can_be_served_back()
    {
        // The two halves are a round trip: the fetcher decides an extension from a
        // content type and the endpoint decides a content type from the extension.
        // One gaining an entry the other lacks would cache pictures that can never
        // be served, which no single-sided test would notice.
        foreach (var contentType in new[] { "image/jpeg", "image/png", "image/webp", "image/gif" })
        {
            var extension = ImageSource.ExtensionFor(contentType);

            Assert.NotNull(extension);
            Assert.Equal(contentType, ImageSource.ContentTypeFor(extension));
        }
    }
}

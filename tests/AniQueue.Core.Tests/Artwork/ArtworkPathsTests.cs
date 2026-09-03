using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Tests.Artwork;

/// <summary>
/// Where a picture lives, on disk and on the wire.
/// </summary>
/// <remarks>
/// The job writes files, the endpoint reads them and the page builds addresses, and
/// none of the three ever speaks to the others — so what is worth asserting here is
/// mostly that the halves agree, and that nothing a request can say reaches a path.
/// </remarks>
public class ArtworkPathsTests
{
    [Theory]
    [InlineData(ImageKind.Poster, ImageRendition.Thumbnail, "thumbnails")]
    [InlineData(ImageKind.Poster, ImageRendition.Full, "posters")]
    [InlineData(ImageKind.Banner, ImageRendition.Full, "banners")]
    [InlineData(ImageKind.ClearLogo, ImageRendition.Full, "logos")]
    [InlineData(ImageKind.Backdrop, ImageRendition.Full, "backdrops")]
    public void Every_picture_has_a_directory_and_it_parses_back(
        ImageKind kind, ImageRendition rendition, string directory)
    {
        Assert.Equal(directory, ArtworkPaths.DirectoryFor(kind, rendition));

        // The round trip is the point: the writer names a directory and the endpoint
        // reads one back, so a picture added to one switch and not the other would
        // cache at an address that can never be resolved.
        Assert.True(ArtworkPaths.TryParseDirectory(directory, out var parsedKind, out var parsedRendition));
        Assert.Equal(kind, parsedKind);
        Assert.Equal(rendition, parsedRendition);
    }

    [Fact]
    public void The_two_poster_renditions_do_not_share_a_directory()
    {
        // Both renditions in one
        // directory is 1,620 files where the argument for splitting by kind said one
        // directory holding all of them is worse to list and worse to sweep — and it
        // put the 145 MB of full-size covers behind a delete that also blanked every
        // list thumbnail.
        Assert.NotEqual(
            ArtworkPaths.DirectoryFor(ImageKind.Poster, ImageRendition.Thumbnail),
            ArtworkPaths.DirectoryFor(ImageKind.Poster, ImageRendition.Full));
    }

    [Fact]
    public void Every_picture_the_enums_declare_has_a_directory()
    {
        // Guards the case the theory above cannot: a fifth kind or a third rendition
        // added with no directory defined for it. Throwing is the intended behaviour,
        // so this fails loudly at the point of the omission rather than at a user's
        // first fetch.
        foreach (var kind in Enum.GetValues<ImageKind>())
        {
            foreach (var rendition in Enum.GetValues<ImageRendition>())
            {
                Assert.False(string.IsNullOrEmpty(ArtworkPaths.DirectoryFor(kind, rendition)));
            }
        }
    }

    [Theory]
    [InlineData("covers")]
    [InlineData("posters/../..")]
    [InlineData("Posters")]
    [InlineData("Thumbnails")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_one_of_those_names_is_refused(string? directory) =>
        Assert.False(ArtworkPaths.TryParseDirectory(directory, out _, out _));

    [Fact]
    public void The_served_address_and_the_file_on_disk_are_built_from_the_same_parts()
    {
        // If these drifted apart every picture would cache successfully and none of
        // them would ever be served, which no single-sided test would notice.
        var url = ArtworkPaths.Url(ImageKind.Poster, ImageRendition.Thumbnail, 42, "deadbeef", ".png");

        Assert.Equal("/art/thumbnails/42/deadbeef.png", url);

        var segments = url.Split('/');
        Assert.True(ArtworkPaths.TryParseDirectory(segments[2], out var kind, out var rendition));
        Assert.True(ArtworkPaths.TryParseSegment(segments[^1], out var hash, out var extension));

        Assert.Equal(
            "thumbnails/42-deadbeef.png",
            ArtworkPaths.RelativePath(kind, rendition, 42, hash, extension));
    }

    [Fact]
    public void The_full_size_cover_is_served_from_its_own_address()
    {
        Assert.Equal(
            "/art/posters/42/deadbeef.png",
            ArtworkPaths.Url(ImageKind.Poster, ImageRendition.Full, 42, "deadbeef", ".png"));
    }

    [Fact]
    public void A_relative_path_uses_forward_slashes_on_every_platform()
    {
        // Compared against what the sweep finds on disk, so both sides have to agree
        // without either normalising — and Windows would otherwise produce one of
        // each.
        Assert.Equal(
            "banners/7-abc.jpg",
            ArtworkPaths.RelativePath(ImageKind.Banner, ImageRendition.Full, 7, "abc", ".jpg"));
    }

    [Theory]
    [InlineData("abc123.jpg", "abc123", ".jpg")]
    [InlineData("ABCDEF.png", "ABCDEF", ".png")]
    public void A_segment_this_application_could_have_produced_is_parsed(
        string segment, string expectedHash, string expectedExtension)
    {
        Assert.True(ArtworkPaths.TryParseSegment(segment, out var hash, out var extension));
        Assert.Equal(expectedHash, hash);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    // No user-supplied string becomes a file path, and this is the check that keeps
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
        Assert.False(ArtworkPaths.TryParseSegment(segment, out var hash, out var extension));
        Assert.Null(hash);
        Assert.Null(extension);
    }

    [Fact]
    public void A_hash_longer_than_one_could_ever_be_is_refused()
    {
        // Not a real attack so much as a bound: the filename is built from this, and
        // there is no reason to let a request name a path component of any length.
        Assert.False(ArtworkPaths.TryParseSegment(new string('a', 65) + ".jpg", out _, out _));
    }
}

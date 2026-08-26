using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Tests.Artwork;

/// <summary>
/// Where a picture lives, on disk and on the wire (D47).
/// </summary>
/// <remarks>
/// The job writes files, the endpoint reads them and the page builds addresses, and
/// none of the three ever speaks to the others — so what is worth asserting here is
/// mostly that the halves agree, and that nothing a request can say reaches a path.
/// </remarks>
public class ArtworkPathsTests
{
    [Theory]
    [InlineData(ImageKind.Poster, "posters")]
    [InlineData(ImageKind.Banner, "banners")]
    [InlineData(ImageKind.ClearLogo, "logos")]
    [InlineData(ImageKind.Backdrop, "backdrops")]
    public void Every_kind_has_a_directory_and_it_parses_back(ImageKind kind, string directory)
    {
        Assert.Equal(directory, ArtworkPaths.DirectoryFor(kind));

        // The round trip is the point: the writer names a directory and the endpoint
        // reads one back, so a kind added to one switch and not the other would cache
        // pictures at an address that can never be resolved.
        Assert.True(ArtworkPaths.TryParseKind(directory, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void Every_kind_the_enum_declares_has_a_directory()
    {
        // Guards the case the theory above cannot: a fifth kind added in 9b with no
        // directory defined for it. Throwing is the intended behaviour, so this
        // fails loudly at the point of the omission rather than at a user's first
        // fetch of a backdrop.
        foreach (var kind in Enum.GetValues<ImageKind>())
        {
            Assert.False(string.IsNullOrEmpty(ArtworkPaths.DirectoryFor(kind)));
        }
    }

    [Theory]
    [InlineData("covers")]
    [InlineData("posters/../..")]
    [InlineData("Posters")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_one_of_those_names_is_refused(string? directory) =>
        Assert.False(ArtworkPaths.TryParseKind(directory, out _));

    [Fact]
    public void The_served_address_and_the_file_on_disk_are_built_from_the_same_parts()
    {
        // If these drifted apart every picture would cache successfully and none of
        // them would ever be served, which no single-sided test would notice.
        var url = ArtworkPaths.Url(ImageKind.Poster, 42, "deadbeef", ".png");

        Assert.Equal("/art/posters/42/deadbeef.png", url);

        var segments = url.Split('/');
        Assert.True(ArtworkPaths.TryParseKind(segments[2], out var kind));
        Assert.True(ArtworkPaths.TryParseSegment(segments[^1], out var hash, out var extension));

        Assert.Equal("posters/42-deadbeef.png", ArtworkPaths.RelativePath(kind, 42, hash, extension));
    }

    [Fact]
    public void A_relative_path_uses_forward_slashes_on_every_platform()
    {
        // Compared against what the sweep finds on disk, so both sides have to agree
        // without either normalising — and Windows would otherwise produce one of
        // each.
        Assert.Equal("banners/7-abc.jpg", ArtworkPaths.RelativePath(ImageKind.Banner, 7, "abc", ".jpg"));
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

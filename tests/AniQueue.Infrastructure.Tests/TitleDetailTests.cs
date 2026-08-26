using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Infrastructure.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// What the detail dialog is handed for one title (D49).
/// </summary>
/// <remarks>
/// The poster fallback is the part worth a database: which of two rendition rows is
/// chosen, and what happens when the better one has not arrived, is a decision made
/// in a query rather than in the markup — and a fresh install spends its first
/// several minutes in exactly the state the middle case describes (D48).
/// </remarks>
public class TitleDetailTests
{
    private const string AniListFormat = "AniList";

    private static ParseResult Fetched(
        string[]? genres = null,
        ParsedStudio[]? studios = null,
        string? description = "Humanity fights back.",
        string? coverImageUrl = "https://s4.anilist.co/medium/bx16498.jpg",
        string? coverImageFullUrl = "https://s4.anilist.co/large/bx16498.jpg") =>
        new()
        {
            Entries =
            [
                new ParsedLibraryEntry
                {
                    Source = AnimeSource.AniList,
                    ExternalIds = [new ExternalIdentifier(AnimeSource.AniList, "16498")],
                    Title = "Shingeki no Kyojin",
                    TitleRomaji = "Shingeki no Kyojin",
                    MediaType = MediaType.Tv,
                    EpisodeCount = 25,
                    EpisodeDurationMinutes = 24,
                    ReleaseYear = 2013,
                    CoverImageUrl = coverImageUrl,
                    CoverImageFullUrl = coverImageFullUrl,
                    Description = description,
                    Genres = genres ?? ["Drama", "Action"],
                    Studios = studios ??
                    [
                        new ParsedStudio("Wit Studio", IsMain: true, IsAnimationStudio: true),
                        new ParsedStudio("Pony Canyon", IsMain: false, IsAnimationStudio: false)
                    ],
                    Status = LibraryStatus.Planning
                }
            ],
            Problems = []
        };

    private static async Task<ImportFixture> SyncedAsync(ParseResult parsed)
    {
        var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(parsed, AniListFormat, Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        return fixture;
    }

    /// <summary>Marks a rendition as cached, which is what the job does on success.</summary>
    private static async Task CacheAsync(ImportFixture fixture, ImageRendition rendition, string hash)
    {
        await using var context = fixture.Database.CreateContext();

        var image = await context.AnimeImages.SingleAsync(i => i.Rendition == rendition);
        image.ContentHash = hash;
        image.FileExtension = ".jpg";
        image.FetchedUrl = image.RemoteUrl;

        await context.SaveChangesAsync();
    }

    private static LibraryService ServiceFor(ImportFixture fixture) =>
        new(fixture.Database.ContextFactory, NullLogger<LibraryService>.Instance);

    [Fact]
    public async Task A_title_arrives_with_everything_the_dialog_argues_with()
    {
        await using var fixture = await SyncedAsync(Fetched());

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        Assert.NotNull(detail);
        Assert.Equal("Shingeki no Kyojin", detail.Title);
        Assert.Equal("Humanity fights back.", detail.Synopsis);
        Assert.Equal(2013, detail.ReleaseYear);

        // Sorted, because a set has no order of its own and the dialog should not
        // reshuffle its chips between two reads of the same title.
        Assert.Equal(["Action", "Drama"], detail.Genres);

        // The main one, not whichever came back first.
        Assert.Equal("Wit Studio", detail.MainStudio);
    }

    [Fact]
    public async Task A_title_with_no_main_studio_flagged_offers_none()
    {
        await using var fixture = await SyncedAsync(Fetched(studios:
            [new ParsedStudio("Pony Canyon", IsMain: false, IsAnimationStudio: false)]));

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        // Null rather than the only company credited: the dialog renders no studio
        // line at all rather than promoting a producer to studio (D25, D49).
        Assert.Null(detail!.MainStudio);
    }

    [Fact]
    public async Task The_full_size_cover_is_preferred_once_it_has_arrived()
    {
        await using var fixture = await SyncedAsync(Fetched());
        await CacheAsync(fixture, ImageRendition.Thumbnail, "aaaa");
        await CacheAsync(fixture, ImageRendition.Full, "bbbb");

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        Assert.Equal("/art/posters/1/bbbb.jpg", detail!.Poster.Url);
    }

    [Fact]
    public async Task Before_the_full_size_cover_arrives_the_list_thumbnail_stands_in()
    {
        // The state every fresh install is in for its first several minutes: 810
        // thumbnails cached and the full-size covers still downloading (D48). A
        // dialog showing a colour block here, beside a list row showing art for the
        // same title, would read as broken rather than as pending.
        await using var fixture = await SyncedAsync(Fetched());
        await CacheAsync(fixture, ImageRendition.Thumbnail, "aaaa");

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        Assert.Equal("/art/thumbnails/1/aaaa.jpg", detail!.Poster.Url);
    }

    [Fact]
    public async Task A_title_with_no_cached_art_at_all_offers_no_picture()
    {
        await using var fixture = await SyncedAsync(Fetched());

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        Assert.False(detail!.Poster.HasImage);
    }

    [Fact]
    public async Task A_title_the_source_said_nothing_about_still_opens()
    {
        // A MyAnimeList-only library, which publishes none of this. The dialog is
        // built to show less rather than to show placeholders, so the query has to
        // return a row rather than refusing one.
        await using var fixture = await SyncedAsync(Fetched(
            genres: [], studios: [], description: null, coverImageFullUrl: null));

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        Assert.NotNull(detail);
        Assert.Empty(detail.Genres);
        Assert.Null(detail.MainStudio);
        Assert.Null(detail.Synopsis);
        Assert.Empty(detail.SynopsisSegments);
    }

    [Fact]
    public async Task A_title_that_is_not_in_this_profiles_library_has_no_detail()
    {
        // The row a user clicked can be removed by a sync between the page rendering
        // and the dialog opening. Null rather than a throw, because that is our
        // timing rather than anything they did (D25).
        await using var fixture = await SyncedAsync(Fetched());

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 999);

        Assert.Null(detail);
    }

    [Fact]
    public async Task The_synopsis_reaches_the_dialog_already_split_into_runs()
    {
        // The seam between the query and the formatter, which is the only place the
        // two meet. Everything about the formatting itself is tested without a
        // database in SynopsisFormatterTests.
        await using var fixture = await SyncedAsync(Fetched(description: "Safe.<br>~!Not safe.!~"));

        var detail = await ServiceFor(fixture).GetTitleDetailAsync(Profile.DefaultProfileId, 1);

        Assert.Collection(
            detail!.SynopsisSegments,
            first => Assert.False(first.IsSpoiler),
            second => Assert.True(second.IsSpoiler));
    }
}

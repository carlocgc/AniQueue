using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// What enrichment writes, and what it must refuse to unwrite.
///
/// The centre of this file is a rule that has no scalar equivalent: <c>Merge</c>
/// rests on "a source never erases a value by not carrying it", and a set has no
/// null to carry that meaning. Every test below that names silence is checking the
/// restatement of it, because the failure it prevents — a MyAnimeList re-import
/// stripping the genres off a shared title — produces no exception, no warning and
/// a green build.
/// </summary>
public class AniListEnrichmentTests
{
    private const string AniListFormat = "AniList";

    private static ParseResult Fetched(
        string aniListId = "16498",
        string title = "Shingeki no Kyojin",
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
                    ExternalIds = [new ExternalIdentifier(AnimeSource.AniList, aniListId)],
                    Title = title,
                    TitleRomaji = title,
                    MediaType = MediaType.Tv,
                    EpisodeCount = 25,
                    EpisodeDurationMinutes = 24,
                    ReleaseYear = 2013,
                    CoverImageUrl = coverImageUrl,
                    CoverImageFullUrl = coverImageFullUrl,
                    Description = description,
                    Genres = genres ?? ["Action", "Drama"],
                    Studios = studios ?? [new ParsedStudio("Wit Studio", IsMain: true, IsAnimationStudio: true)],
                    Status = LibraryStatus.Planning
                }
            ],
            Problems = []
        };

    /// <summary>The same title as a MyAnimeList export sees it: none of the above.</summary>
    private static ParseResult Exported(string malId = "16498", string title = "Attack on Titan") =>
        new()
        {
            Entries =
            [
                new ParsedLibraryEntry
                {
                    Source = AnimeSource.MyAnimeList,
                    ExternalIds = [new ExternalIdentifier(AnimeSource.MyAnimeList, malId)],
                    Title = title,
                    MediaType = MediaType.Tv,
                    EpisodeCount = 25,
                    Status = LibraryStatus.Completed,
                    EpisodesWatched = 25
                }
            ],
            Problems = []
        };

    private static async Task SyncAsync(ImportFixture fixture, ParseResult parsed, string format = AniListFormat)
    {
        var preview = await fixture.Service.PreviewAsync(parsed, format, Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);
    }

    /// <summary>Gives the title a MyAnimeList identifier, as a real bridge would.</summary>
    private static async Task BridgeToMyAnimeListAsync(ImportFixture fixture, string malId = "16498")
    {
        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        context.AnimeExternalIds.Add(new AnimeExternalId
        {
            AnimeId = anime.Id,
            Source = AnimeSource.MyAnimeList,
            ExternalId = malId
        });

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_synced_title_lands_with_its_genres_studios_and_synopsis()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched());

        await using var context = fixture.Database.CreateContext();

        var anime = await context.Anime
            .Include(a => a.Genres).ThenInclude(g => g.Genre)
            .Include(a => a.Studios).ThenInclude(s => s.Studio)
            .SingleAsync();

        Assert.Equal("Humanity fights back.", anime.Description);
        Assert.Equal(["Action", "Drama"], anime.Genres.Select(g => g.Genre!.Name).Order());

        var studio = Assert.Single(anime.Studios);
        Assert.Equal("Wit Studio", studio.Studio!.Name);
        Assert.True(studio.IsMain);
        Assert.True(studio.Studio.IsAnimationStudio);
    }

    [Fact]
    public async Task A_later_MyAnimeList_import_does_not_strip_the_genres_AniList_supplied()
    {
        // The failure this whole rule exists for, and the reason it is written twice.
        // A MyAnimeList export publishes no genres at all. If an empty incoming set
        // read as "this title has no genres" rather than as silence, the consolidating
        // user's ordinary Tuesday — sync AniList, re-import MyAnimeList — would blank
        // the genres on every title the two lists share, with nothing raised and
        // nothing logged.
        await using var fixture = await ImportFixture.CreateAsync();

        // The export is what this user trusts for tracking, which is what makes the
        // last assertion a witness that the import ran at all. Under the default seat
        // AniList would keep its own status, and a status that had not moved would
        // prove nothing either way.
        fixture.Options.PrimarySource = AnimeSource.MyAnimeList;

        await SyncAsync(fixture, Fetched());
        await BridgeToMyAnimeListAsync(fixture);
        await SyncAsync(fixture, Exported(), "MyAnimeList XML");

        await using var context = fixture.Database.CreateContext();

        var anime = await context.Anime
            .Include(a => a.Genres).ThenInclude(g => g.Genre)
            .Include(a => a.Studios)
            .SingleAsync();

        Assert.Equal(["Action", "Drama"], anime.Genres.Select(g => g.Genre!.Name).Order());
        Assert.Single(anime.Studios);
        Assert.Equal("Humanity fights back.", anime.Description);

        // The import was real rather than ignored wholesale, which is what makes the
        // assertions above mean something.
        var entry = await context.LibraryEntries.SingleAsync();
        Assert.Equal(LibraryStatus.Completed, entry.Status);
    }

    [Fact]
    public async Task A_genre_the_source_has_dropped_goes_rather_than_accumulating()
    {
        // Replacement rather than union. A union would mean a title mis-tagged at the
        // source could be corrected there and never here, and the set could only ever
        // grow.
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched(genres: ["Action", "Drama", "Ecchi"]));
        await SyncAsync(fixture, Fetched(genres: ["Action", "Drama"]));

        await using var context = fixture.Database.CreateContext();

        var anime = await context.Anime
            .Include(a => a.Genres).ThenInclude(g => g.Genre)
            .SingleAsync();

        Assert.Equal(["Action", "Drama"], anime.Genres.Select(g => g.Genre!.Name).Order());

        // The Genre row itself survives — it is shared vocabulary, not this title's
        // property, and deleting it would be a second title's problem.
        Assert.Contains(context.Genres, g => g.Name == "Ecchi");
    }

    [Fact]
    public async Task A_title_gaining_only_genres_is_not_reported_as_unchanged()
    {
        // The trap that would have made most of this phase inert. A preview item with
        // no changes is skipped outright at commit, so anything the preview cannot
        // see is something the commit will never write. Here everything else about
        // the title is identical and only the genres are new.
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched(genres: []));

        var preview = await fixture.Service.PreviewAsync(
            Fetched(genres: ["Action"]), AniListFormat, Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Update, preview.Items[0].Action);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        Assert.Single(context.AnimeGenres);
    }

    [Fact]
    public async Task A_library_that_predates_the_full_size_cover_gains_one_without_changing_otherwise()
    {
        // A library that already has thumbnails and no full-size covers: a thumbnail row
        // per title and nothing else new. The preview has to see the missing
        // rendition, or the commit skips the title and the dialog never gets a poster.
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched(coverImageFullUrl: null, genres: [], studios: [], description: null));

        await using (var before = fixture.Database.CreateContext())
        {
            Assert.Equal(ImageRendition.Thumbnail, (await before.AnimeImages.SingleAsync()).Rendition);
        }

        var preview = await fixture.Service.PreviewAsync(
            Fetched(genres: [], studios: [], description: null), AniListFormat, Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Update, preview.Items[0].Action);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var images = await context.AnimeImages.OrderBy(i => i.Rendition).ToListAsync();

        Assert.Equal(2, images.Count);
        Assert.Equal("https://s4.anilist.co/medium/bx16498.jpg", images[0].RemoteUrl);
        Assert.Equal("https://s4.anilist.co/large/bx16498.jpg", images[1].RemoteUrl);
    }

    [Fact]
    public async Task The_two_renditions_fail_and_retry_independently()
    {
        // Separate rows rather than extra columns, so a full-size cover that has not
        // arrived does not hold up the thumbnail that has. Replacing one address
        // clears that row's failure state and leaves the other row's alone.
        //
        // The synopsis changes too, and that is not incidental: a cover URL that has
        // merely rotated is deliberately not reported as a change, so an item whose
        // *only* difference is an image address is Unchanged and skipped outright.
        // Giving the entry a second, reportable difference is what gets the commit as
        // far as the image rows at all.
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched());

        await using (var setup = fixture.Database.CreateContext())
        {
            foreach (var image in await setup.AnimeImages.ToListAsync())
            {
                image.FailedAt = DateTimeOffset.UtcNow;
                image.AttemptCount = 5;
            }

            await setup.SaveChangesAsync();
        }

        await SyncAsync(fixture, Fetched(
            description: "Humanity fights back, revised.",
            coverImageFullUrl: "https://s4.anilist.co/large/bx16498-v2.jpg"));

        await using var context = fixture.Database.CreateContext();

        var thumbnail = await context.AnimeImages.SingleAsync(i => i.Rendition == ImageRendition.Thumbnail);
        var full = await context.AnimeImages.SingleAsync(i => i.Rendition == ImageRendition.Full);

        Assert.Equal(5, thumbnail.AttemptCount);
        Assert.NotNull(thumbnail.FailedAt);

        Assert.Equal(0, full.AttemptCount);
        Assert.Null(full.FailedAt);
    }

    [Fact]
    public async Task A_genre_two_titles_share_is_stored_once()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched("16498", "Shingeki no Kyojin", genres: ["Action", "Drama"]));
        await SyncAsync(fixture, Fetched("21", "One Piece", genres: ["Action", "Adventure"]));

        await using var context = fixture.Database.CreateContext();

        Assert.Equal(3, await context.Genres.CountAsync());
        Assert.Equal(4, await context.AnimeGenres.CountAsync());
    }

    [Fact]
    public async Task A_title_recredited_to_a_different_lead_studio_moves_the_flag()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await SyncAsync(fixture, Fetched(studios:
        [
            new ParsedStudio("Wit Studio", IsMain: true, IsAnimationStudio: true),
            new ParsedStudio("MAPPA", IsMain: false, IsAnimationStudio: true)
        ]));

        await SyncAsync(fixture, Fetched(studios:
        [
            new ParsedStudio("Wit Studio", IsMain: false, IsAnimationStudio: true),
            new ParsedStudio("MAPPA", IsMain: true, IsAnimationStudio: true)
        ]));

        await using var context = fixture.Database.CreateContext();

        var main = await context.AnimeStudios
            .Include(s => s.Studio)
            .SingleAsync(s => s.IsMain);

        Assert.Equal("MAPPA", main.Studio!.Name);
        Assert.Equal(2, await context.AnimeStudios.CountAsync());
    }

    [Fact]
    public async Task A_source_that_does_not_outrank_another_may_fill_a_gap_but_not_overwrite()
    {
        // Precedence, reaching the collections. AniList fills genres a
        // MyAnimeList-primary library does not have, because filling a gap is what
        // a demoted source is always allowed to do — and then stops, because the
        // second sync is an overwrite rather than a gap.
        await using var fixture = await ImportFixture.CreateAsync();

        // One row both sources identify, which is the only situation where rank means
        // anything at all.
        await SyncAsync(fixture, Fetched(genres: []));
        await BridgeToMyAnimeListAsync(fixture);

        fixture.Options.PrimarySource = AnimeSource.MyAnimeList;

        await SyncAsync(fixture, Fetched(genres: ["Action"]));
        await SyncAsync(fixture, Fetched(genres: ["Comedy"]));

        await using var context = fixture.Database.CreateContext();

        var anime = await context.Anime
            .Include(a => a.Genres).ThenInclude(g => g.Genre)
            .SingleAsync();

        var genre = Assert.Single(anime.Genres);
        Assert.Equal("Action", genre.Genre!.Name);
    }
}

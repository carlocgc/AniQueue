using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The catalogue fields a sync brings and a file export does not: episode duration,
/// release year, cover art and the second title variant.
///
/// These matter beyond tidiness. There is a runtime filter, a runtime sort
/// and *Under 2h* / *Under 6h* / decade chips against columns nothing had ever
/// populated, so every one of them was inert in a real installation. They start
/// working when these fields do.
///
/// Exercised through the <see cref="ParseResult"/> seam, as a sync will be: the
/// MyAnimeList parser cannot produce any of these values, which is precisely the
/// asymmetry the tests below are about.
/// </summary>
public class CatalogueFieldsTests
{
    private const string AniListFormat = "AniList";

    private static ParseResult Fetched(
        string aniListId,
        string title,
        string? englishTitle = "Attack on Titan",
        int? durationMinutes = 24,
        int? releaseYear = 2013,
        string? coverImageUrl = "https://example.invalid/cover.jpg") =>
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
                    TitleEnglish = englishTitle,
                    TitleNative = "進撃の巨人",
                    MediaType = MediaType.Tv,
                    EpisodeCount = 25,
                    EpisodeDurationMinutes = durationMinutes,
                    ReleaseYear = releaseYear,
                    CoverImageUrl = coverImageUrl,
                    Status = LibraryStatus.Planning
                }
            ],
            Problems = []
        };

    /// <summary>The same title as a MyAnimeList export sees it: no duration, year or art.</summary>
    private static ParseResult Exported(string malId, string title) =>
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

    [Fact]
    public async Task A_created_title_keeps_the_duration_year_and_art_it_arrived_with()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan"), AniListFormat, Profile.DefaultProfileId);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal(24, anime.EpisodeDurationMinutes);
        Assert.Equal(2013, anime.ReleaseYear);
        Assert.Equal("https://example.invalid/cover.jpg", (await context.AnimeImages.SingleAsync()).RemoteUrl);
        Assert.Equal("Attack on Titan", anime.TitleEnglish);
        Assert.Equal("進撃の巨人", anime.TitleNative);
    }

    [Fact]
    public async Task A_later_file_import_does_not_erase_what_the_sync_knew()
    {
        // The consolidating user's ordinary Tuesday: sync AniList, then re-import a
        // MyAnimeList export. The export carries none of these fields, and a
        // catalogue write that treated absent as empty would blank the runtime data
        // for every title the two lists share — turning those filters back off.
        await using var fixture = await ImportFixture.CreateAsync();

        var synced = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan"), AniListFormat, Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(synced, Profile.DefaultProfileId);

        // The export matches the same row through its MyAnimeList identifier only
        // once that identifier exists, so link the two the way a real bridge does.
        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await setup.Anime.SingleAsync();
            setup.AnimeExternalIds.Add(new AnimeExternalId
            {
                AnimeId = anime.Id,
                Source = AnimeSource.MyAnimeList,
                ExternalId = "16498"
            });

            await setup.SaveChangesAsync();
        }

        var imported = await fixture.Service.PreviewAsync(
            Exported("16498", "Shingeki no Kyojin"), "MyAnimeList XML", Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(imported, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var updated = await context.Anime.SingleAsync();

        Assert.Equal(24, updated.EpisodeDurationMinutes);
        Assert.Equal(2013, updated.ReleaseYear);
        Assert.Equal("https://example.invalid/cover.jpg", (await context.AnimeImages.SingleAsync()).RemoteUrl);

        // The tracking data did land, so this is a real import rather than one that
        // was ignored wholesale.
        var entry = await context.LibraryEntries.SingleAsync();
        Assert.Equal(LibraryStatus.Completed, entry.Status);
    }

    /// <summary>
    /// A source publishing one unlabelled name does not overwrite a display title
    /// resolved from labelled variants.
    /// </summary>
    /// <remarks>
    /// The bug this pins down: the display title was resolved from the <i>incoming
    /// entry's</i> variants, and a MyAnimeList export has none, so it fell through to
    /// that export's single name — while the very next lines merged the variants and
    /// kept AniList's. The row was left holding a Title that disagreed with its own
    /// TitleRomaji, and the change did not last either, because the title-language
    /// setting rewrites Title from the row's variants. Resolving from the merged row
    /// makes the import and that setting agree by construction.
    /// </remarks>
    [Fact]
    public async Task A_file_import_does_not_rename_a_title_the_sync_labelled()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var synced = await fixture.Service.PreviewAsync(
            Fetched("16498", "Shingeki no Kyojin"), AniListFormat, Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(synced, Profile.DefaultProfileId);

        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await setup.Anime.SingleAsync();
            setup.AnimeExternalIds.Add(new AnimeExternalId
            {
                AnimeId = anime.Id,
                Source = AnimeSource.MyAnimeList,
                ExternalId = "16498"
            });

            await setup.SaveChangesAsync();
        }

        // The same show under the name MyAnimeList publishes, with no variants at all.
        var imported = await fixture.Service.PreviewAsync(
            Exported("16498", "Attack on Titan"), "MyAnimeList XML", Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(imported, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var updated = await context.Anime.SingleAsync();

        // The profile reads romaji, and the row still holds AniList's romaji, so the
        // displayed name is unchanged — and, crucially, agrees with the variant it
        // was resolved from.
        Assert.Equal("Shingeki no Kyojin", updated.TitleRomaji);
        Assert.Equal("Shingeki no Kyojin", updated.Title);
    }

    /// <summary>
    /// A title only one source describes still takes that source's name, however it
    /// is ranked — otherwise a MyAnimeList-only library could never be renamed by
    /// re-importing a corrected export.
    /// </summary>
    [Fact]
    public async Task A_file_import_still_names_a_title_no_other_source_describes()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var first = await fixture.Service.PreviewAsync(
            Exported("16498", "Shingeki no Kyojin"), "MyAnimeList XML", Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(first, Profile.DefaultProfileId);

        var renamed = await fixture.Service.PreviewAsync(
            Exported("16498", "Attack on Titan"), "MyAnimeList XML", Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(renamed, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var updated = await context.Anime.SingleAsync();

        Assert.Equal("Attack on Titan", updated.Title);
    }

    [Fact]
    public async Task Gaining_art_is_a_change_but_a_moved_cover_url_alone_is_not()
    {
        // A cover URL that merely changed is nearly always the same picture behind a
        // rotated CDN path. Reporting it would make an otherwise idle sync render as
        // a library-wide list of updates needing review, which is exactly the churn
        // must not happen, because an unchanged sync writes nothing.
        await using var fixture = await ImportFixture.CreateAsync();

        var first = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan", coverImageUrl: null),
            AniListFormat,
            Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(first, Profile.DefaultProfileId);

        var gainsArt = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan"), AniListFormat, Profile.DefaultProfileId);

        var gained = Assert.Single(gainsArt.Items);
        Assert.Equal(ImportAction.Update, gained.Action);
        Assert.Contains("Adds cover art", gained.Changes);

        await fixture.Service.CommitAsync(gainsArt, Profile.DefaultProfileId);

        var moved = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan", coverImageUrl: "https://example.invalid/moved.jpg"),
            AniListFormat,
            Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Unchanged, Assert.Single(moved.Items).Action);
    }

    [Fact]
    public async Task A_second_fetch_of_the_same_data_changes_nothing()
    {
        // Idempotency, restated for the fields added here: none of them may report a
        // difference against a value they just wrote, or every sync would show the
        // whole library as updated.
        await using var fixture = await ImportFixture.CreateAsync();

        var first = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan"), AniListFormat, Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(first, Profile.DefaultProfileId);

        var second = await fixture.Service.PreviewAsync(
            Fetched("16498", "Attack on Titan"), AniListFormat, Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Unchanged, Assert.Single(second.Items).Action);
        Assert.False(second.HasApplicableChanges);
    }
}

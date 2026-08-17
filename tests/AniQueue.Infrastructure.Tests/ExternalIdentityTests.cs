using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// D17's bridge, tested through the pipeline rather than the schema.
///
/// These are the tests that stop Phase 5 regressing into the failure it was
/// designed around: a library imported from one service, met by a sync speaking
/// another, matching nothing and conflicting on every title. Against a real
/// 752-entry export that would be 750 hand decisions, with nobody present to make
/// them during an unattended sync.
///
/// They go through <see cref="IImportService.PreviewAsync(ParseResult, string, int, IProgress{Core.Progress.OperationProgress}?, CancellationToken)"/>
/// rather than a stream, because no parser yet emits two identifiers — the AniList
/// one lands in Phase 5b. That overload is exactly the seam a sync will use, so
/// exercising it here tests the path rather than a stand-in for it.
/// </summary>
public class ExternalIdentityTests
{
    private const string AniListFormat = "AniList";

    /// <summary>An entry as an AniList sync would produce it: its own id, plus idMal.</summary>
    private static ParseResult AniListEntry(
        string aniListId,
        string? malId,
        string title,
        LibraryStatus status = LibraryStatus.Planning,
        int? episodes = 12) =>
        new()
        {
            Entries =
            [
                new ParsedLibraryEntry
                {
                    Source = AnimeSource.AniList,
                    ExternalIds = malId is null
                        ? [new ExternalIdentifier(AnimeSource.AniList, aniListId)]
                        :
                        [
                            new ExternalIdentifier(AnimeSource.AniList, aniListId),
                            new ExternalIdentifier(AnimeSource.MyAnimeList, malId)
                        ],
                    Title = title,
                    MediaType = MediaType.Tv,
                    EpisodeCount = episodes,
                    Status = status
                }
            ],
            Problems = []
        };

    [Fact]
    public async Task An_AniList_entry_matches_a_MyAnimeList_row_through_its_MAL_id()
    {
        // The bridge. Without it this is a Create, and the user ends up with two
        // rows for one show — once per title in their library.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(
                setup, "Shingeki no Kyojin", AnimeSource.MyAnimeList, "16498");

            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            await setup.SaveChangesAsync();
        }

        var preview = await fixture.Service.PreviewAsync(
            AniListEntry(aniListId: "16498", malId: "16498", "Attack on Titan"),
            AniListFormat,
            Profile.DefaultProfileId);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportAction.Update, item.Action);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());

        // And the AniList id is now stored, so the next sync matches directly.
        var identifiers = await context.AnimeExternalIds.OrderBy(x => x.Source).ToListAsync();
        Assert.Equal(2, identifiers.Count);
        Assert.Equal(AnimeSource.MyAnimeList, identifiers[0].Source);
        Assert.Equal(AnimeSource.AniList, identifiers[1].Source);
    }

    [Fact]
    public async Task The_two_services_do_not_issue_the_same_number()
    {
        // Measured on real data: Shingeki no Kyojin is 16498 on both, but its second
        // season is AniList 20958 and MyAnimeList 25777. Code that treated one id as
        // the other would look correct across a sample and then quietly map a sequel
        // onto an unrelated title, so the divergent case is tested explicitly.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(
                setup, "Shingeki no Kyojin Season 2", AnimeSource.MyAnimeList, "25777");

            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            await setup.SaveChangesAsync();
        }

        var preview = await fixture.Service.PreviewAsync(
            AniListEntry(aniListId: "20958", malId: "25777", "Attack on Titan Season 2"),
            AniListFormat,
            Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Update, Assert.Single(preview.Items).Action);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());
        Assert.Equal(2, await context.AnimeExternalIds.CountAsync());
    }

    [Fact]
    public async Task An_entry_with_no_MAL_counterpart_is_created()
    {
        // idMal is null for 6 of 753 entries in the measured library. Those cannot
        // bridge and must simply be new titles, not conflicts.
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            AniListEntry(aniListId: "12345", malId: null, "AniList Exclusive"),
            AniListFormat,
            Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Create, Assert.Single(preview.Items).Action);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var identifier = await context.AnimeExternalIds.SingleAsync();
        Assert.Equal(AnimeSource.AniList, identifier.Source);
    }

    [Fact]
    public async Task Syncing_twice_changes_nothing_the_second_time()
    {
        // Idempotency across the bridge specifically: the first run writes the
        // AniList id onto a MyAnimeList row, and the second must find it rather
        // than reporting the same link again.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(
                setup, "Shingeki no Kyojin", AnimeSource.MyAnimeList, "16498");

            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            await setup.SaveChangesAsync();
        }

        var first = await fixture.Service.PreviewAsync(
            AniListEntry("16498", "16498", "Shingeki no Kyojin"),
            AniListFormat,
            Profile.DefaultProfileId);

        await fixture.Service.CommitAsync(first, Profile.DefaultProfileId);

        var second = await fixture.Service.PreviewAsync(
            AniListEntry("16498", "16498", "Shingeki no Kyojin"),
            AniListFormat,
            Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Unchanged, Assert.Single(second.Items).Action);
        Assert.False(second.HasApplicableChanges);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(2, await context.AnimeExternalIds.CountAsync());
    }

    [Fact]
    public async Task Identifiers_pointing_at_two_different_titles_become_a_conflict()
    {
        // Two local rows are really one show. Picking whichever identifier was tried
        // first would silently merge against one and orphan the other, so the user
        // decides. There is no merge surface, which is exactly why this is not
        // resolved automatically.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "From MAL", AnimeSource.MyAnimeList, "16498");
            await SeedData.CreateAnimeAsync(setup, "From AniList", AnimeSource.AniList, "20958");
        }

        var preview = await fixture.Service.PreviewAsync(
            AniListEntry(aniListId: "20958", malId: "16498", "Attack on Titan"),
            AniListFormat,
            Profile.DefaultProfileId);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportAction.Conflict, item.Action);
        Assert.Contains("2 different titles", item.ConflictReason);
    }

    [Fact]
    public async Task One_payload_claiming_an_identifier_twice_is_reported_not_applied()
    {
        // AniList holds split and duplicate entries pointing at a single idMal. The
        // second write would violate the uniqueness index and abort the entire
        // import, so it is caught while previewing and named against the entry that
        // caused it.
        await using var fixture = await ImportFixture.CreateAsync();

        var parsed = new ParseResult
        {
            Entries =
            [
                AniListEntry("111", "16498", "First claimant").Entries[0],
                AniListEntry("222", "16498", "Second claimant").Entries[0]
            ],
            Problems = []
        };

        var preview = await fixture.Service.PreviewAsync(parsed, AniListFormat, Profile.DefaultProfileId);

        Assert.Equal(ImportAction.Create, preview.Items[0].Action);
        Assert.Equal(ImportAction.Conflict, preview.Items[1].Action);
        Assert.Contains("First claimant", preview.Items[1].ConflictReason);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        // The commit survives rather than aborting, and only the first entry lands.
        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());
    }

    [Fact]
    public async Task A_rejected_payload_cannot_be_committed()
    {
        // A partial fetch is indistinguishable from mass deletion, so a sync that
        // fails midway must reject rather than report a partial success.
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            ParseResult.Rejected("The fetch failed after two of five pages."),
            AniListFormat,
            Profile.DefaultProfileId);

        Assert.True(preview.IsFileRejected);
        Assert.Empty(preview.Items);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.CommitAsync(preview, Profile.DefaultProfileId));
    }
}

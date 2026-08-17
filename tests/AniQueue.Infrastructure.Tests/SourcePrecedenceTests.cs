using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// D18: on a title two sources both describe, the higher-ranked one owns the
/// user's tracking data.
///
/// The failure this prevents is specific and only appears once consolidation is
/// supported. A show sits on both lists; it is finished and scored on the
/// higher-ranked service and imported, and the queue slot is correctly released.
/// The other service still says Planning, and unconditional last-writer-wins lets
/// its next run revert the lot — every interval, forever, with the deliberate act
/// losing each time because the scheduled writer is always the last writer.
/// </summary>
public class SourcePrecedenceTests
{
    private static ParseResult Payload(
        AnimeSource source,
        IReadOnlyList<ExternalIdentifier> identifiers,
        string title,
        LibraryStatus status,
        int? score,
        int watched,
        MediaType mediaType = MediaType.Tv,
        int? episodes = 12) =>
        new()
        {
            Entries =
            [
                new ParsedLibraryEntry
                {
                    Source = source,
                    ExternalIds = identifiers,
                    Title = title,
                    MediaType = mediaType,
                    EpisodeCount = episodes,
                    Status = status,
                    UserScore = score,
                    EpisodesWatched = watched
                }
            ],
            Problems = []
        };

    private static ParseResult FromMal(
        string malId,
        string title,
        LibraryStatus status,
        int? score = null,
        int watched = 0) =>
        Payload(
            AnimeSource.MyAnimeList,
            [new ExternalIdentifier(AnimeSource.MyAnimeList, malId)],
            title,
            status,
            score,
            watched);

    /// <summary>
    /// An AniList entry as a sync produces it — its own id *and* idMal — so it
    /// bridges onto a MyAnimeList-imported row (D17) rather than creating a second
    /// title. Precedence only has something to decide once both sources describe
    /// one row, so the bridge is a precondition of these tests rather than
    /// incidental to them.
    /// </summary>
    private static ParseResult FromAniList(
        string aniListId,
        string malId,
        string title,
        LibraryStatus status,
        int? score = null,
        int watched = 0,
        MediaType mediaType = MediaType.Tv,
        int? episodes = 12) =>
        Payload(
            AnimeSource.AniList,
            [
                new ExternalIdentifier(AnimeSource.AniList, aniListId),
                new ExternalIdentifier(AnimeSource.MyAnimeList, malId)
            ],
            title,
            status,
            score,
            watched,
            mediaType,
            episodes);

    private static async Task RankAsync(SqliteTestDatabase database, AnimeSource source, int rank)
    {
        await using var context = database.CreateContext();

        context.SourceSyncSettings.Add(new SourceSyncSettings
        {
            ProfileId = Profile.DefaultProfileId,
            Source = source,
            PrecedenceRank = rank,
            IsEnabled = true
        });

        await context.SaveChangesAsync();
    }

    private static async Task ApplyAsync(ImportFixture fixture, ParseResult parsed)
    {
        var preview = await fixture.Service.PreviewAsync(parsed, "test", Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);
    }

    [Fact]
    public async Task A_lower_ranked_source_cannot_revert_what_a_higher_ranked_one_recorded()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.MyAnimeList, rank: 0);
        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 1);

        // The authoritative list says finished and scored.
        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Completed, score: 9, watched: 12));

        // The secondary list is stale and still says planning, unscored. It bridges
        // onto the same row through idMal, which is exactly what makes the two
        // sources contest one entry.
        await ApplyAsync(fixture, FromAniList("777", "268", "Golden Boy", LibraryStatus.Planning));

        await using var context = fixture.Database.CreateContext();
        var entry = await context.LibraryEntries.SingleAsync();

        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(9, entry.UserScore);
        Assert.Equal(12, entry.EpisodesWatched);
        Assert.Equal(AnimeSource.MyAnimeList, entry.LastWrittenBySource);
    }

    [Fact]
    public async Task A_higher_ranked_source_overwrites_a_lower_ranked_one()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.MyAnimeList, rank: 1);
        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 0);

        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Planning));

        await ApplyAsync(fixture, FromAniList(
            "777", "268", "Golden Boy", LibraryStatus.Completed, score: 8, watched: 12));

        await using var context = fixture.Database.CreateContext();
        var entry = await context.LibraryEntries.SingleAsync();

        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(8, entry.UserScore);
        Assert.Equal(AnimeSource.AniList, entry.LastWrittenBySource);
    }

    [Fact]
    public async Task With_nothing_configured_the_last_writer_still_wins()
    {
        // The single-tracker case D13 optimises for pays nothing for precedence
        // existing. This is the behaviour that shipped before D18, asserted so a
        // future change to the default cannot pass unnoticed.
        await using var fixture = await ImportFixture.CreateAsync();

        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Completed, score: 9, watched: 12));

        await ApplyAsync(fixture, FromAniList("777", "268", "Golden Boy", LibraryStatus.Planning));

        await using var context = fixture.Database.CreateContext();
        var entry = await context.LibraryEntries.SingleAsync();

        Assert.Equal(LibraryStatus.Planning, entry.Status);
        Assert.Null(entry.UserScore);
    }

    [Fact]
    public async Task One_source_ranked_alone_never_blocks_itself()
    {
        // A source cannot outrank itself, so repeated syncs from a single service
        // keep applying however it is ranked.
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 5);

        await ApplyAsync(fixture, FromAniList("16498", "16498", "Attack on Titan", LibraryStatus.Planning));

        await ApplyAsync(fixture, FromAniList(
            "16498", "16498", "Attack on Titan", LibraryStatus.Completed, score: 10, watched: 25));

        await using var context = fixture.Database.CreateContext();
        var entry = await context.LibraryEntries.SingleAsync();

        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(10, entry.UserScore);
    }

    [Fact]
    public async Task A_blocked_source_still_contributes_catalogue_metadata()
    {
        // Precedence guards the user's tracking data, not facts about the title.
        // AniList carries fields a MyAnimeList export simply does not, and refusing
        // those because of a ranking would lose data for no reason.
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.MyAnimeList, rank: 0);
        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 1);

        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Completed, score: 9, watched: 12));

        // The lower-ranked source knows the episode count and media type that a
        // MyAnimeList export never carried.
        await ApplyAsync(fixture, FromAniList(
            "777",
            "268",
            "Golden Boy",
            LibraryStatus.Planning,
            mediaType: MediaType.Ova,
            episodes: 6));

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();
        var entry = await context.LibraryEntries.SingleAsync();

        Assert.Equal(MediaType.Ova, anime.MediaType);
        Assert.Equal(6, anime.EpisodeCount);

        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(9, entry.UserScore);
        Assert.Equal(AnimeSource.MyAnimeList, entry.LastWrittenBySource);
    }
}

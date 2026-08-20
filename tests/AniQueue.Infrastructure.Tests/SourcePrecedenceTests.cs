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
        int watched = 0,
        MediaType mediaType = MediaType.Tv,

        // Overridable because a real export often carries no count, and D29 turns on
        // the difference between a field a source left empty and one it disagrees
        // about. A helper that always supplied 12 could only ever exercise the second.
        int? episodes = 12) =>
        Payload(
            AnimeSource.MyAnimeList,
            [new ExternalIdentifier(AnimeSource.MyAnimeList, malId)],
            title,
            status,
            score,
            watched,
            mediaType,
            episodes);

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

    /// <summary>
    /// A lower-ranked source fills gaps and does not settle disagreements (D29).
    /// </summary>
    /// <remarks>
    /// This test used to assert that a blocked source wrote catalogue metadata
    /// outright, justified by "AniList carries fields a MyAnimeList export simply
    /// does not" — an argument about gaps that the implementation applied as
    /// last-write-wins. The distinction it was missing is the whole of D29: an
    /// episode count nobody had is filled by whoever has it, while a media type the
    /// two sources disagree about goes to the one the user named primary, so the
    /// answer does not depend on which import ran last.
    /// </remarks>
    [Fact]
    public async Task A_blocked_source_fills_gaps_without_overruling_the_primary()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.MyAnimeList, rank: 0);
        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 1);

        // No episode count in the export, and Tv where AniList will say Ova.
        await ApplyAsync(fixture, FromMal(
            "268", "Golden Boy", LibraryStatus.Completed, score: 9, watched: 12, episodes: null));

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

        // The gap: a MyAnimeList export carries no episode count, so refusing
        // AniList's would lose data for no reason.
        Assert.Equal(6, anime.EpisodeCount);

        // The disagreement: MyAnimeList said Tv and AniList says Ova. The primary
        // keeps it, and would keep it whichever order the two were imported in.
        Assert.Equal(MediaType.Tv, anime.MediaType);

        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(9, entry.UserScore);
        Assert.Equal(AnimeSource.MyAnimeList, entry.LastWrittenBySource);
    }

    /// <summary>
    /// The primary still corrects what a secondary wrote, so precedence is a
    /// ranking rather than a first-writer-wins lock.
    /// </summary>
    [Fact]
    public async Task The_primary_source_overrules_a_value_a_secondary_wrote()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 0);
        await RankAsync(fixture.Database, AnimeSource.MyAnimeList, rank: 1);

        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Completed));

        await ApplyAsync(fixture, FromAniList(
            "777",
            "268",
            "Golden Boy",
            LibraryStatus.Planning,
            mediaType: MediaType.Ova,
            episodes: 6));

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal(MediaType.Ova, anime.MediaType);
    }

    /// <summary>
    /// A title only one source knows about is always that source's to correct,
    /// whatever its rank — which is what keeps a single-tracker library, and a
    /// re-import of a corrected export, behaving as they always did.
    /// </summary>
    [Fact]
    public async Task A_source_may_always_correct_a_title_no_other_source_describes()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await RankAsync(fixture.Database, AnimeSource.AniList, rank: 0);
        await RankAsync(fixture.Database, AnimeSource.MyAnimeList, rank: 1);

        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Completed, mediaType: MediaType.Tv));
        await ApplyAsync(fixture, FromMal("268", "Golden Boy", LibraryStatus.Completed, mediaType: MediaType.Ova));

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal(MediaType.Ova, anime.MediaType);
    }
}

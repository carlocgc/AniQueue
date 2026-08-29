using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The database-level guarantees the rest of the application is allowed to assume.
/// These run against real SQLite because the constraints under test do not exist
/// in the EF InMemory provider at all.
/// </summary>
public class ConstraintTests
{
    [Fact]
    public async Task A_queue_slot_holds_one_title()
    {
        // That is all a slot can be. The XOR check constraint that let it
        // hold a group instead is gone, and so is the group.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Slayers");

        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0, animeId: anime.Id));
        await context.SaveChangesAsync();

        var stored = await context.QueueItems.SingleAsync();
        Assert.Equal(anime.Id, stored.AnimeId);
    }

    [Fact]
    public async Task The_same_title_cannot_occupy_two_queue_slots()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Gunbuster");

        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0, animeId: anime.Id));
        await context.SaveChangesAsync();

        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 1, animeId: anime.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// Values on a run item come from an external model and are untrusted
    /// data. These constraints are the last line if validation upstream ever has a
    /// gap, so they are worth asserting rather than assuming — the entity had no
    /// coverage at all until its configuration changed.
    /// </summary>
    [Theory]
    [InlineData(-0.1)]        // confidence is a probability
    [InlineData(1.5)]
    public async Task A_run_item_outside_the_permitted_ranges_is_rejected(double confidence)
    {
        // The rank cases — 0 and -1 against CK_RecommendationRunItems_RankPositive —
        // went with the column. Confidence is the range left at this boundary,
        // and the boundary is still worth a test: these values arrive from an external
        // model, so a validation gap upstream must not be able to persist nonsense.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var (run, anime) = await CreateRunAsync(context);

        run.Items.Add(new RecommendationRunItem
        {
            AnimeId = anime.Id,
            PredictedScore = 8.0,
            Confidence = confidence
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_valid_run_item_is_stored_against_its_title()
    {
        // A score is always against one title; there is no group variant and
        // no exclusive-or constraint to satisfy.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var (run, anime) = await CreateRunAsync(context);

        run.Items.Add(new RecommendationRunItem
        {
            AnimeId = anime.Id,
            PredictedScore = 8.4,
            Confidence = 0.75,
            Reason = "Matches your comedy history"
        });

        await context.SaveChangesAsync();

        var stored = await context.Set<RecommendationRunItem>().SingleAsync();
        Assert.Equal(anime.Id, stored.AnimeId);
        Assert.Equal(8.4, stored.PredictedScore);
        Assert.Equal(0.75, stored.Confidence);
    }

    private static async Task<(RecommendationRun Run, Anime Anime)> CreateRunAsync(AniQueueDbContext context)
    {
        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Nichijou");

        var run = new RecommendationRun
        {
            ProfileId = profile.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderName = "ManualJson",
            CandidateCount = 1,
            ResultCount = 1
        };

        context.RecommendationRuns.Add(run);
        await context.SaveChangesAsync();

        return (run, anime);
    }

    [Fact]
    public async Task Duplicate_source_identifiers_are_rejected()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        await SeedData.CreateAnimeAsync(context, "Golden Boy", AnimeSource.MyAnimeList, "268");

        await Assert.ThrowsAsync<DbUpdateException>(
            () => SeedData.CreateAnimeAsync(context, "Golden Boy (duplicate)", AnimeSource.MyAnimeList, "268"));
    }

    [Fact]
    public async Task The_same_identifier_from_a_different_source_is_allowed()
    {
        // MAL id 268 and AniList id 268 are unrelated titles; uniqueness is per source.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        await SeedData.CreateAnimeAsync(context, "From MAL", AnimeSource.MyAnimeList, "268");
        await SeedData.CreateAnimeAsync(context, "From AniList", AnimeSource.AniList, "268");

        Assert.Equal(2, await context.Anime.CountAsync());
    }

    [Fact]
    public async Task Many_manual_entries_without_a_source_identifier_are_allowed()
    {
        // A hand-added title has no identifier row at all, so manual entries cannot
        // collide with each other and the uniqueness index needs no filter.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        await SeedData.CreateAnimeAsync(context, "Hand-added one");
        await SeedData.CreateAnimeAsync(context, "Hand-added two");
        await SeedData.CreateAnimeAsync(context, "Hand-added three");

        Assert.Equal(3, await context.Anime.CountAsync());
        Assert.Equal(0, await context.AnimeExternalIds.CountAsync());
    }

    [Fact]
    public async Task A_title_cannot_hold_two_identifiers_from_one_source()
    {
        // Nothing legitimately issues two MyAnimeList ids for one show, so a second
        // is evidence that two sources disagree about identity. That is a conflict
        // for the user to resolve, never a row to write.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var anime = await SeedData.CreateAnimeAsync(context, "Golden Boy", AnimeSource.MyAnimeList, "268");

        context.AnimeExternalIds.Add(new AnimeExternalId
        {
            AnimeId = anime.Id,
            Source = AnimeSource.MyAnimeList,
            ExternalId = "999"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task One_title_may_be_identified_by_several_services()
    {
        // A title AniList knows carries a MyAnimeList id too, and
        // holding both is what lets an import in either order match rather than
        // duplicate.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var anime = await SeedData.CreateAnimeAsync(context, "Attack on Titan", AnimeSource.AniList, "16498");

        context.AnimeExternalIds.Add(new AnimeExternalId
        {
            AnimeId = anime.Id,
            Source = AnimeSource.MyAnimeList,
            ExternalId = "16498"
        });

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.AnimeExternalIds.CountAsync(x => x.AnimeId == anime.Id));
    }

    [Fact]
    public async Task Deleting_a_title_takes_its_identifiers_with_it()
    {
        // Identity is meaningless without the title it identifies, and a stranded
        // row would silently claim an identifier no longer in use.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var anime = await SeedData.CreateAnimeAsync(context, "Golden Boy", AnimeSource.MyAnimeList, "268");

        context.Anime.Remove(anime);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.AnimeExternalIds.CountAsync());
    }

    [Fact]
    public async Task A_profile_cannot_hold_two_entries_for_the_same_title()
    {
        // The guarantee that makes re-importing the same export idempotent.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Nichijou");

        context.LibraryEntries.Add(SeedData.Entry(profile.Id, anime.Id));
        await context.SaveChangesAsync();

        context.LibraryEntries.Add(SeedData.Entry(profile.Id, anime.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(0)]   // MAL writes 0 for "unscored"; that must not become a real score
    [InlineData(11)]
    [InlineData(-1)]
    public async Task Scores_outside_one_to_ten_are_rejected(int score)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Konosuba");

        context.LibraryEntries.Add(SeedData.Entry(profile.Id, anime.Id, userScore: score));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task An_unscored_entry_is_allowed()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Planning title");

        context.LibraryEntries.Add(SeedData.Entry(profile.Id, anime.Id, userScore: null));
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.LibraryEntries.CountAsync());
    }
}

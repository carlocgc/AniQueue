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
        // Since D15 that is all a slot can be. The XOR check constraint that let it
        // hold a franchise instead is gone, along with the franchise slot itself —
        // a franchise is a grouping and an action, not a thing you watch.
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

    [Fact]
    public async Task Deleting_a_franchise_leaves_its_titles_queued()
    {
        // Dissolving a grouping is a curation decision about labels. It must not
        // silently empty the queue — under the old model the franchise's slot went
        // with it, taking the user's ordering along.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var franchise = await SeedData.CreateFranchiseAsync(context, "Slayers");
        var anime = await SeedData.CreateAnimeAsync(context, "Slayers Next");

        anime.FranchiseId = franchise.Id;
        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0, animeId: anime.Id));
        await context.SaveChangesAsync();

        context.Franchises.Remove(franchise);
        await context.SaveChangesAsync();

        var slot = await context.QueueItems.SingleAsync();
        Assert.Equal(anime.Id, slot.AnimeId);
    }

    /// <summary>
    /// Values on a run item come from an external model, which §6 treats as untrusted
    /// data. These constraints are the last line if validation upstream ever has a
    /// gap, so they are worth asserting rather than assuming — the entity had no
    /// coverage at all until D16 touched its configuration.
    /// </summary>
    [Theory]
    [InlineData(0, 0.5)]      // rank must be 1-based
    [InlineData(-1, 0.5)]
    [InlineData(1, -0.1)]     // confidence is a probability
    [InlineData(1, 1.5)]
    public async Task A_run_item_outside_the_permitted_ranges_is_rejected(int rank, double confidence)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var (run, anime) = await CreateRunAsync(context);

        run.Items.Add(new RecommendationRunItem
        {
            AnimeId = anime.Id,
            Rank = rank,
            PredictedScore = 8.0,
            Confidence = confidence
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_valid_run_item_is_stored_against_its_title()
    {
        // Since D16 a placement is always one title; there is no franchise variant
        // and no exclusive-or constraint to satisfy.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var (run, anime) = await CreateRunAsync(context);

        run.Items.Add(new RecommendationRunItem
        {
            AnimeId = anime.Id,
            Rank = 1,
            PredictedScore = 8.4,
            Confidence = 0.75,
            Reason = "Matches your comedy history"
        });

        await context.SaveChangesAsync();

        var stored = await context.Set<RecommendationRunItem>().SingleAsync();
        Assert.Equal(anime.Id, stored.AnimeId);
        Assert.Equal(1, stored.Rank);
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
        // Why the uniqueness index is filtered: unfiltered, every manual entry
        // would collide with every other one on (Manual, NULL).
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        await SeedData.CreateAnimeAsync(context, "Hand-added one");
        await SeedData.CreateAnimeAsync(context, "Hand-added two");
        await SeedData.CreateAnimeAsync(context, "Hand-added three");

        Assert.Equal(3, await context.Anime.CountAsync());
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

    [Fact]
    public async Task Dissolving_a_franchise_keeps_its_titles()
    {
        // Franchises are a grouping decision, not ownership. Removing the grouping
        // must never remove the library.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var franchise = await SeedData.CreateFranchiseAsync(context, "Slayers");
        var anime = await SeedData.CreateAnimeAsync(context, "Slayers Next");
        anime.FranchiseId = franchise.Id;
        await context.SaveChangesAsync();

        context.Franchises.Remove(franchise);
        await context.SaveChangesAsync();

        var survivor = await context.Anime.SingleAsync();
        Assert.Equal("Slayers Next", survivor.Title);
        Assert.Null(survivor.FranchiseId);
    }
}

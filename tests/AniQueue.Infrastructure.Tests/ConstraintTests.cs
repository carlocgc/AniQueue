using AniQueue.Core.Domain;
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
    public async Task Queue_slot_may_hold_a_franchise()
    {
        // The case that forced the queue onto its own table (D1): the brief's
        // LibraryEntry.QueuePosition could never have represented this, because a
        // franchise has no LibraryEntry row.
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var franchise = await SeedData.CreateFranchiseAsync(context, "Slayers");

        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0, franchiseId: franchise.Id));
        await context.SaveChangesAsync();

        var stored = await context.QueueItems.SingleAsync();
        Assert.True(stored.IsFranchise);
        Assert.Equal(franchise.Id, stored.FranchiseId);
        Assert.Null(stored.AnimeId);
    }

    [Fact]
    public async Task Queue_slot_referencing_neither_anime_nor_franchise_is_rejected()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, position: 0));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Queue_slot_referencing_both_anime_and_franchise_is_rejected()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);
        var anime = await SeedData.CreateAnimeAsync(context, "Gunbuster");
        var franchise = await SeedData.CreateFranchiseAsync(context, "Gunbuster");

        context.QueueItems.Add(
            SeedData.QueueSlot(profile.Id, position: 0, animeId: anime.Id, franchiseId: franchise.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
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

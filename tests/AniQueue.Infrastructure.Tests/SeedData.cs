using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;

namespace AniQueue.Infrastructure.Tests;

/// <summary>Minimal builders so tests state only what they are actually about.</summary>
internal static class SeedData
{
    public static async Task<Profile> CreateProfileAsync(AniQueueDbContext context, string name = "Test")
    {
        var profile = new Profile { Name = name, CreatedAt = DateTimeOffset.UtcNow };
        context.Profiles.Add(profile);
        await context.SaveChangesAsync();
        return profile;
    }

    public static async Task<Anime> CreateAnimeAsync(
        AniQueueDbContext context,
        string title,
        AnimeSource source = AnimeSource.Manual,
        string? sourceAnimeId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var anime = new Anime
        {
            Title = title,
            Source = source,
            SourceAnimeId = sourceAnimeId,
            CreatedAt = now,
            UpdatedAt = now
        };

        context.Anime.Add(anime);
        await context.SaveChangesAsync();
        return anime;
    }

    public static async Task<Franchise> CreateFranchiseAsync(AniQueueDbContext context, string name)
    {
        var now = DateTimeOffset.UtcNow;
        var franchise = new Franchise { Name = name, CreatedAt = now, UpdatedAt = now };
        context.Franchises.Add(franchise);
        await context.SaveChangesAsync();
        return franchise;
    }

    public static QueueItem QueueSlot(int profileId, int position, int? animeId = null, int? franchiseId = null) =>
        new()
        {
            ProfileId = profileId,
            Position = position,
            AnimeId = animeId,
            FranchiseId = franchiseId,
            AddedAt = DateTimeOffset.UtcNow
        };

    public static LibraryEntry Entry(int profileId, int animeId, int? userScore = null) =>
        new()
        {
            ProfileId = profileId,
            AnimeId = animeId,
            UserScore = userScore,
            DateAdded = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };
}

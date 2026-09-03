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

            // An identifier is a row rather than a column now, and a manual
            // entry has none at all rather than a null one.
            ExternalIds = sourceAnimeId is null
                ? []
                : [new AnimeExternalId { Source = source, ExternalId = sourceAnimeId }],
            CreatedAt = now,
            UpdatedAt = now
        };

        context.Anime.Add(anime);
        await context.SaveChangesAsync();
        return anime;
    }

    public static QueueItem QueueSlot(int profileId, int position, int animeId) =>
        new()
        {
            ProfileId = profileId,
            Position = position,
            AnimeId = animeId,
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

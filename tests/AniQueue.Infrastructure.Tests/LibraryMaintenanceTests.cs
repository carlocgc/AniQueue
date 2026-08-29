using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;
using AniQueue.Infrastructure.Artwork;
using AniQueue.Infrastructure.Library;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// Deleting everything, and what "everything" deliberately excludes.
/// </summary>
public class LibraryMaintenanceTests
{
    private static LibraryMaintenance Create(SqliteTestDatabase database, string? artRoot = null) =>
        new(
            database.ContextFactory,
            new CoverArtStore(Options.Create(new AniQueueDatabaseOptions
            {
                Path = artRoot is null ? ":memory:" : Path.Combine(artRoot, "aniqueue.db")
            })),
            NullLogger<LibraryMaintenance>.Instance);

    private static async Task<Profile> LibraryOfThreeAsync(SqliteTestDatabase database)
    {
        await using var context = database.CreateContext();

        var profile = await SeedData.CreateProfileAsync(context);

        var first = await SeedData.CreateAnimeAsync(context, "Hinamatsuri");
        var second = await SeedData.CreateAnimeAsync(context, "Serial Experiments Lain");
        var third = await SeedData.CreateAnimeAsync(context, "Slayers");

        context.LibraryEntries.AddRange(
            SeedData.Entry(profile.Id, first.Id, userScore: 9),
            SeedData.Entry(profile.Id, second.Id),
            SeedData.Entry(profile.Id, third.Id));

        context.QueueItems.Add(SeedData.QueueSlot(profile.Id, 1, first.Id));

        context.JobRuns.Add(new JobRun
        {
            TaskKey = "sync",
            UnitKey = string.Empty,
            Trigger = JobTrigger.Manual,
            Outcome = JobOutcome.Succeeded,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        });

        await context.SaveChangesAsync();

        return profile;
    }

    [Fact]
    public async Task The_counts_are_what_the_library_holds()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var profile = await LibraryOfThreeAsync(database);

        var contents = await Create(database).GetContentsAsync(profile.Id);

        Assert.Equal(3, contents.Titles);
        Assert.Equal(1, contents.Queued);
    }

    [Fact]
    public async Task Deleting_everything_empties_the_library_the_queue_and_the_history()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var profile = await LibraryOfThreeAsync(database);

        var gone = await Create(database).DeleteEverythingAsync(profile.Id);

        Assert.Equal(3, gone.Titles);
        Assert.Equal(1, gone.Queued);

        await using var context = database.CreateContext();

        Assert.Empty(await context.Anime.ToListAsync());
        Assert.Empty(await context.LibraryEntries.ToListAsync());
        Assert.Empty(await context.QueueItems.ToListAsync());
        Assert.Empty(await context.JobRuns.ToListAsync());
    }

    /// <summary>
    /// The profile and its settings survive, which is what "your settings stay" means.
    /// </summary>
    /// <remarks>
    /// <c>ProfileSettings</c> hangs off <c>Profile</c>, so deleting the profile would
    /// reset the theme and the title language this action promises to keep — and the
    /// initializer would mint a replacement profile on the next start.
    /// </remarks>
    [Fact]
    public async Task Deleting_everything_keeps_the_profile_and_its_settings()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();

        await new DatabaseInitializer(
            database.ContextFactory,
            Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

        await using (var setup = database.CreateContext())
        {
            var settings = await setup.ProfileSettings.SingleAsync();
            settings.Theme = ThemePreference.Dark;
            settings.PreferredTitleLanguage = TitleLanguage.English;
            await setup.SaveChangesAsync();
        }

        await Create(database).DeleteEverythingAsync(Profile.DefaultProfileId);

        await using var context = database.CreateContext();

        Assert.Single(await context.Profiles.ToListAsync());

        var kept = await context.ProfileSettings.SingleAsync();
        Assert.Equal(ThemePreference.Dark, kept.Theme);
        Assert.Equal(TitleLanguage.English, kept.PreferredTitleLanguage);
    }

    /// <summary>
    /// The vocabularies go with the titles they described.
    /// </summary>
    /// <remarks>
    /// Left behind they would be a backlog filter offering genres that nothing in the
    /// library has.
    /// </remarks>
    [Fact]
    public async Task Deleting_everything_takes_the_genres_and_studios_with_it()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();

        Profile profile;

        await using (var setup = database.CreateContext())
        {
            profile = await SeedData.CreateProfileAsync(setup);
            var anime = await SeedData.CreateAnimeAsync(setup, "Hinamatsuri");

            var genre = new Genre { Name = "Comedy" };
            var studio = new Studio { Name = "Feel", IsAnimationStudio = true };

            setup.Genres.Add(genre);
            setup.Studios.Add(studio);
            await setup.SaveChangesAsync();

            setup.AnimeGenres.Add(new AnimeGenre { AnimeId = anime.Id, GenreId = genre.Id });
            setup.AnimeStudios.Add(new AnimeStudio { AnimeId = anime.Id, StudioId = studio.Id, IsMain = true });
            setup.LibraryEntries.Add(SeedData.Entry(profile.Id, anime.Id));
            await setup.SaveChangesAsync();
        }

        await Create(database).DeleteEverythingAsync(profile.Id);

        await using var context = database.CreateContext();

        Assert.Empty(await context.Genres.ToListAsync());
        Assert.Empty(await context.Studios.ToListAsync());
        Assert.Empty(await context.AnimeGenres.ToListAsync());
        Assert.Empty(await context.AnimeStudios.ToListAsync());
    }

    /// <summary>
    /// Deleting the artwork removes the files and leaves the rows, so the cache heals
    /// itself.
    /// </summary>
    /// <remarks>
    /// A row whose cached file has gone is already a state the artwork pass repairs.
    /// Clearing the rows as well would make this a re-fetch of everything from
    /// nothing rather than a way to reclaim the space.
    /// </remarks>
    [Fact]
    public async Task Deleting_the_artwork_removes_the_files_and_keeps_the_rows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aniqueue-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "art", "cover"));
        await File.WriteAllTextAsync(Path.Combine(root, "art", "cover", "1-abc.jpg"), "bytes");

        try
        {
            await using var database = await SqliteTestDatabase.CreateAsync();

            await using (var setup = database.CreateContext())
            {
                var anime = await SeedData.CreateAnimeAsync(setup, "Hinamatsuri");

                setup.AnimeImages.Add(new AnimeImage
                {
                    AnimeId = anime.Id,
                    Kind = ImageKind.Poster,
                    Rendition = ImageRendition.Thumbnail,
                    RemoteUrl = "https://example.invalid/cover.jpg",
                    ContentHash = "abc",
                    FileExtension = "jpg"
                });

                await setup.SaveChangesAsync();
            }

            var maintenance = Create(database, root);

            Assert.Equal(1, await maintenance.DeleteArtworkAsync());

            await using var context = database.CreateContext();

            Assert.Single(await context.AnimeImages.ToListAsync());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "art"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

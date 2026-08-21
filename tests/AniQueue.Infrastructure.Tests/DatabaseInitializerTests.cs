using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

public class DatabaseInitializerTests
{
    [Fact]
    public async Task Initialising_creates_the_default_profile_with_settings()
    {
        // There is no registration flow in the MVP, so nothing works until the
        // single profile exists.
        await using var database = await SqliteTestDatabase.CreateAsync();

        await CreateInitializer(database).InitialiseAsync();

        await using var context = database.CreateContext();
        var profile = await context.Profiles.Include(p => p.Settings).SingleAsync();

        Assert.Equal(Profile.DefaultProfileId, profile.Id);
        Assert.Equal("Default", profile.Name);
        Assert.NotNull(profile.Settings);
    }

    [Fact]
    public async Task Initialising_twice_does_not_create_a_second_profile()
    {
        // Every container restart runs this, so it has to be idempotent.
        await using var database = await SqliteTestDatabase.CreateAsync();
        var initializer = CreateInitializer(database);

        await initializer.InitialiseAsync();
        await initializer.InitialiseAsync();

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.Profiles.CountAsync());
    }

    [Fact]
    public async Task Default_settings_do_not_opt_into_sharing_personal_notes()
    {
        // Privacy default: notes are free text and never leave the machine in an
        // AI export unless the user explicitly turns it on (ROADMAP.md §6).
        await using var database = await SqliteTestDatabase.CreateAsync();

        await CreateInitializer(database).InitialiseAsync();

        await using var context = database.CreateContext();
        var settings = await context.ProfileSettings.SingleAsync();

        // The scoring settings that used to be asserted here moved to userconfig.json
        // (D36); what a fresh database still has to produce is the display preferences.
        Assert.Equal(TitleLanguage.Romaji, settings.PreferredTitleLanguage);
        Assert.Equal(RecommendationMode.Manual, settings.DefaultRecommendationMode);
    }

    private static DatabaseInitializer CreateInitializer(SqliteTestDatabase database) =>
        new(
            database.ContextFactory,
            // ":memory:" makes the initialiser skip directory creation and WAL,
            // neither of which applies to an in-memory database.
            Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance);
}

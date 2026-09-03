using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The theme is read once per page load, before the document is sent, so what
/// matters is that a read always has an answer and that a save is the answer the
/// next read gets.
/// </summary>
public class AppearanceTests
{
    private static async Task<SqliteTestDatabase> InitialisedAsync()
    {
        var database = await SqliteTestDatabase.CreateAsync();

        await new DatabaseInitializer(
            database.ContextFactory,
            Microsoft.Extensions.Options.Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

        return database;
    }

    [Fact]
    public async Task A_chosen_theme_is_what_the_next_read_returns()
    {
        await using var database = await InitialisedAsync();
        var appearance = new Appearance(database.ContextFactory);

        await appearance.SaveThemeAsync(Profile.DefaultProfileId, ThemePreference.Dark);

        Assert.Equal(
            ThemePreference.Dark,
            await appearance.GetThemeAsync(Profile.DefaultProfileId));
    }

    /// <summary>
    /// A profile with no settings row follows the browser rather than failing.
    /// </summary>
    /// <remarks>
    /// This is read while the document is being built, so there is nowhere for an
    /// exception to go that is not a blank page — and "no row" is the ordinary state
    /// of a profile nothing has written settings for yet.
    /// </remarks>
    [Fact]
    public async Task A_profile_with_no_settings_row_follows_the_browser()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();

        Assert.Equal(
            ThemePreference.System,
            await new Appearance(database.ContextFactory).GetThemeAsync(Profile.DefaultProfileId));
    }

    /// <summary>
    /// Saving a theme for a profile whose settings row is gone creates one.
    /// </summary>
    /// <remarks>
    /// The row is made by the initializer, so this is not the ordinary path — but a
    /// save that assumed it and threw would lose a preference over a row nothing
    /// else needs, and creating it costs one branch.
    /// </remarks>
    [Fact]
    public async Task A_theme_can_be_saved_after_the_settings_row_has_gone()
    {
        await using var database = await InitialisedAsync();

        await using (var setup = database.CreateContext())
        {
            setup.ProfileSettings.RemoveRange(setup.ProfileSettings);
            await setup.SaveChangesAsync();
        }

        var appearance = new Appearance(database.ContextFactory);

        await appearance.SaveThemeAsync(Profile.DefaultProfileId, ThemePreference.Light);

        Assert.Equal(
            ThemePreference.Light,
            await appearance.GetThemeAsync(Profile.DefaultProfileId));
    }
}

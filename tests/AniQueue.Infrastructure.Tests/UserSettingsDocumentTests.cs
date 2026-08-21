using System.Reflection;
using Microsoft.Extensions.Configuration;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Settings;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// Guards the one mistake this design makes easy: adding a setting to
/// <see cref="UserSettings"/> and forgetting the line that writes it.
/// </summary>
/// <remarks>
/// Without this, the symptom is a setting that saves without error, survives until
/// the next save, and then silently reverts — because it was never in the file that
/// the next save regenerated. That is a bug nobody reports usefully.
/// </remarks>
public class UserSettingsDocumentTests
{
    [Fact]
    public void Every_setting_has_a_line_in_the_file()
    {
        var written = UserSettingsDocument.Keys;

        var properties = typeof(UserSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(UserSettings.Defaults))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(properties.Count, written.Count);
    }

    [Fact]
    public void A_default_document_parses_as_configuration()
    {
        // Every line commented out has to leave a file the JSON provider accepts —
        // otherwise a first boot produces a file whose settings are all silently
        // absent, which is the failure the one-key-per-line format exists to prevent.
        var text = Render(UserSettings.Defaults);

        var path = Path.Combine(Path.GetTempPath(), $"aniqueue-doc-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, text);

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            Assert.Empty(configuration.AsEnumerable().Where(pair => pair.Value is not null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_document_with_every_setting_changed_parses_as_configuration()
    {
        // The other end of the same property: a file where nothing is commented out
        // still has to be valid, trailing comma and all.
        var text = Render(new UserSettings
        {
            SyncEnabled = false,
            AniListUserName = "hibari",
            ScoringHistorySize = 25,
            ScoringCandidateLimit = 50,
            ScoringReturnTop = 20,
            ScoringIncludePersonalNotes = true,
            DatabaseBusyTimeoutSeconds = 60
        });

        var path = Path.Combine(Path.GetTempPath(), $"aniqueue-doc-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, text);

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            Assert.Equal("false", configuration["Sync:Enabled"], ignoreCase: true);
            Assert.Equal("hibari", configuration["Sync:AniList:UserName"]);
            Assert.Equal("25", configuration["Scoring:HistorySize"]);
            Assert.Equal("50", configuration["Scoring:CandidateLimit"]);
            Assert.Equal("20", configuration["Scoring:ReturnTop"]);
            Assert.Equal("true", configuration["Scoring:IncludePersonalNotes"], ignoreCase: true);
            Assert.Equal("60", configuration["Database:BusyTimeoutSeconds"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_username_with_a_quote_in_it_cannot_break_the_file()
    {
        // Values are serialised rather than interpolated. Nobody's AniList name looks
        // like this, but a settings file that a value can corrupt is one an application
        // can be locked out of by its own save.
        var text = Render(UserSettings.Defaults with { AniListUserName = "he said \"hello\"\\" });

        var path = Path.Combine(Path.GetTempPath(), $"aniqueue-doc-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, text);

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            Assert.Equal("he said \"hello\"\\", configuration["Sync:AniList:UserName"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>What the file would say for these settings.</summary>
    private static string Render(UserSettings settings) => UserSettingsDocument.Render(settings);
}

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
    public void A_default_document_round_trips_every_default()
    {
        // What a first boot leaves. It has to parse, and reading it back has to produce
        // the settings it was rendered from — a file that says something subtly
        // different from what is running is worse than no file.
        var text = Render(UserSettings.Defaults);

        var path = Path.Combine(Path.GetTempPath(), $"aniqueue-doc-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, text);

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            Assert.Equal("true", configuration["Sync:Enabled"], ignoreCase: true);
            Assert.Equal("200", configuration["Scoring:HistorySize"]);

            // An empty string rather than null, because this is the line a fresh
            // installation edits by hand and replacing "" with a name needs no thought
            // about quoting.
            Assert.Contains("\"Sync:AniList:UserName\": \"\"", text, StringComparison.Ordinal);

            // Unset stays unset. A null in the file must not read back as a value, or
            // "rank everything" would silently become a cap.
            Assert.Null(configuration["Scoring:CandidateLimit"]);
            Assert.Null(configuration["Scoring:ReturnTop"]);
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
            ScoringReturnTop = 20
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

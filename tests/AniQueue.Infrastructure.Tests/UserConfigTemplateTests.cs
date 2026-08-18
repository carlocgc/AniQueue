using System.Text.Json;
using AniQueue.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The settings file written on first boot (D20).
///
/// These tests touch the real file system, which the rest of this project avoids.
/// It is the subject: what is being asserted is behaviour toward a volume that may
/// already hold the operator's work, or may not be writable at all.
/// </summary>
public class UserConfigTemplateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"aniqueue-userconfig-{Guid.NewGuid():N}");

    private static UserConfigTemplate Template() => new(NullLogger<UserConfigTemplate>.Instance);

    private string Path_ => Path.Combine(_directory, UserConfigTemplate.FileName);

    /// <summary>
    /// Reads the file the way the JSON configuration provider does — tolerating the
    /// comments and trailing commas it allows — so a template it would reject fails
    /// here rather than at somebody's next startup.
    /// </summary>
    /// <remarks>
    /// Parsed rather than run through a real <c>ConfigurationBuilder</c>, which
    /// would mean adding a configuration package to this project for one assertion.
    /// The mapping from top-level properties to configuration keys is the
    /// framework's own behaviour and is not what these tests are for; that the file
    /// declares no properties at all is.
    /// </remarks>
    private static JsonDocument Read(string path) =>
        JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_first_boot_leaves_a_file_to_find()
    {
        Assert.True(await Template().EnsureExistsAsync(_directory));
        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public async Task The_template_configures_nothing()
    {
        // The point of the whole exercise. This file is read *last*, so a template
        // shipping real values would override the Sync__AniList__UserName an
        // operator set in their compose file — on a machine where nobody had opened
        // the file at all. Commented out, it can only take effect once somebody
        // chooses to uncomment a line.
        await Template().EnsureExistsAsync(_directory);

        using var document = Read(Path_);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateObject());
    }

    [Theory]
    [InlineData("\"Sync:Enabled\"")]
    [InlineData("\"Sync:AniList:UserName\"")]
    [InlineData("\"Database:BusyTimeoutSeconds\"")]
    public async Task Uncommenting_any_single_line_leaves_a_valid_file(string key)
    {
        // The promise the template makes, tested one line at a time. Each setting is
        // written as a full colon-separated key precisely so this holds: uncommenting
        // out of a nested block would leave its closing braces behind, and a
        // malformed settings file stops AniQueue from starting — a poor property for
        // the file an operator edits when something is already wrong.
        await Template().EnsureExistsAsync(_directory);

        var edited = string.Join('\n', (await File.ReadAllTextAsync(Path_))
            .Split('\n')
            .Select(line => line.Contains(key, StringComparison.Ordinal)
                ? line.Replace("// ", string.Empty, StringComparison.Ordinal)
                : line));

        await File.WriteAllTextAsync(Path_, edited);

        using var document = Read(Path_);
        var property = Assert.Single(document.RootElement.EnumerateObject());

        Assert.Equal(key.Trim('"'), property.Name);
    }

    [Fact]
    public async Task Uncommenting_every_line_leaves_a_valid_file_too()
    {
        // Order matters as little as count: whichever lines survive, the trailing
        // comma the last one carries is allowed rather than fatal.
        await Template().EnsureExistsAsync(_directory);

        var edited = string.Join('\n', (await File.ReadAllTextAsync(Path_))
            .Split('\n')
            .Select(line => line.TrimStart().StartsWith("// \"", StringComparison.Ordinal)
                ? line.Replace("// ", string.Empty, StringComparison.Ordinal)
                : line));

        await File.WriteAllTextAsync(Path_, edited);

        using var document = Read(Path_);

        Assert.True(document.RootElement.GetProperty("Sync:Enabled").GetBoolean());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("Sync:AniList:UserName").GetString());
        Assert.Equal(30, document.RootElement.GetProperty("Database:BusyTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task An_existing_file_is_never_touched()
    {
        // Including one the operator emptied on purpose. Overwriting settings
        // somebody edited by hand is the one unforgivable behaviour here.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path_, "{ }");

        Assert.False(await Template().EnsureExistsAsync(_directory));
        Assert.Equal("{ }", await File.ReadAllTextAsync(Path_));
    }

    [Fact]
    public async Task A_missing_directory_is_created()
    {
        // A bind-mounted volume is empty on first run, and may not exist at all when
        // the path came from configuration rather than from Docker.
        Assert.False(Directory.Exists(_directory));

        Assert.True(await Template().EnsureExistsAsync(_directory));
    }

    [Fact]
    public async Task An_unwritable_location_is_a_warning_rather_than_a_failure()
    {
        // §9: a non-root container cannot write to a root-owned bind mount. Refusing
        // to start over a convenience file would turn a hint into an outage.
        var blocked = Path.Combine(_directory, "not-a-directory");

        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(blocked, "this is a file, not a directory");

        Assert.False(await Template().EnsureExistsAsync(blocked));
    }

    [Fact]
    public async Task No_data_directory_means_nothing_to_write()
    {
        Assert.False(await Template().EnsureExistsAsync(string.Empty));
    }
}

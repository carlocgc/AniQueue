using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The file half of D36: what gets written, what a reload makes current, and what
/// happens when the volume will not take a write.
/// </summary>
public class UserSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"aniqueue-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, UserConfigStatus.FileName);

    /// <summary>
    /// A store wired the way the application wires it: the same file added to the
    /// configuration chain that the store writes to, so a save and a read are talking
    /// about one file rather than two.
    /// </summary>
    private (UserSettingsStore Store, IConfigurationRoot Configuration) Create(
        IEnumerable<KeyValuePair<string, string?>>? environment = null)
    {
        Directory.CreateDirectory(_directory);

        var builder = new ConfigurationBuilder();

        if (environment is not null)
        {
            builder.AddInMemoryCollection(environment);
        }

        var status = new UserConfigStatus { Path = SettingsPath };

        // Added last and allowed to fail, exactly as Program.cs adds it — the point of
        // testing through the real provider is that its idea of what parses is the one
        // that matters, not a second implementation of ours.
        builder.AddJsonFile(source =>
        {
            source.Path = SettingsPath;
            source.Optional = true;
            source.ReloadOnChange = false;
            source.OnLoadException = context =>
            {
                context.Ignore = true;
                status.Fail(context.Exception.GetBaseException().Message);
            };

            source.ResolveFileProvider();
        });

        var configuration = builder.Build();

        return (new UserSettingsStore(configuration, status, NullLogger<UserSettingsStore>.Instance), configuration);
    }

    [Fact]
    public async Task A_first_boot_leaves_a_file_that_configures_nothing()
    {
        var (store, configuration) = Create();

        Assert.True(await store.EnsureExistsAsync());
        Assert.True(File.Exists(SettingsPath));

        // The property D20 made load-bearing and D36 keeps: a template that shipped
        // real values would override whatever an operator had set elsewhere, on a
        // machine where nobody had opened the file.
        configuration.Reload();

        Assert.Empty(configuration.AsEnumerable().Where(pair => pair.Value is not null));
    }

    [Fact]
    public async Task The_file_it_writes_names_every_key_it_accepts()
    {
        var (store, _) = Create();

        await store.EnsureExistsAsync();

        var text = await File.ReadAllTextAsync(SettingsPath);

        // A key nobody can find is a key nobody can use when the pages are unreachable,
        // which is the whole reason the file exists.
        Assert.All(
            new[]
            {
                "Sync:Enabled",
                "Sync:AniList:UserName",
                "Scoring:HistorySize",
                "Scoring:CandidateLimit",
                "Scoring:ReturnTop",
                "Scoring:IncludePersonalNotes",
                "Database:BusyTimeoutSeconds"
            },
            key => Assert.Contains(key, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten_by_a_boot()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{ }");

        var (store, _) = Create();

        // Including a file somebody emptied deliberately. It is their work.
        Assert.False(await store.EnsureExistsAsync());
        Assert.Equal("{ }", await File.ReadAllTextAsync(SettingsPath));
    }

    [Fact]
    public async Task Saving_makes_a_value_current_without_waiting_for_a_watcher()
    {
        // The reload is explicit precisely because the watcher is not dependable on the
        // bind mounts this application is deployed onto (D20), so the assertion that
        // matters is that the value is live the moment the save returns.
        var (store, configuration) = Create();

        var result = await store.SaveAsync(UserSettings.Defaults with { AniListUserName = "hibari" });

        Assert.True(result.Saved);
        Assert.Equal("hibari", configuration["Sync:AniList:UserName"]);
        Assert.Equal("hibari", store.Read().AniListUserName);
    }

    [Fact]
    public async Task A_value_left_at_its_default_is_written_commented_out()
    {
        // So that a default improved in a later version reaches an installation whose
        // file predates it, rather than being pinned by whatever was true when the file
        // was created.
        var (store, configuration) = Create();

        await store.SaveAsync(UserSettings.Defaults with { AniListUserName = "hibari" });

        var text = await File.ReadAllTextAsync(SettingsPath);

        Assert.Contains("\"Sync:AniList:UserName\": \"hibari\"", text, StringComparison.Ordinal);
        Assert.Contains("// \"Sync:Enabled\"", text, StringComparison.Ordinal);
        Assert.Null(configuration["Sync:Enabled"]);
    }

    [Fact]
    public async Task Setting_something_back_to_its_default_stops_setting_it()
    {
        var (store, configuration) = Create();

        await store.SaveAsync(UserSettings.Defaults with { SyncEnabled = false });
        Assert.Equal("False", configuration["Sync:Enabled"], ignoreCase: true);

        await store.SaveAsync(UserSettings.Defaults with { SyncEnabled = true });

        // Not "written as true" — written as nothing, which means the same thing today
        // and keeps meaning the right thing if the default ever changes.
        Assert.Null(configuration["Sync:Enabled"]);
        Assert.True(store.Read().SyncEnabled);
    }

    [Fact]
    public async Task A_save_of_one_page_does_not_clear_what_another_page_wrote()
    {
        // The file is regenerated whole, so every caller has to pass the settings it is
        // not changing straight back through. This is the test that says so out loud,
        // because getting it wrong loses somebody's AniList account when they change a
        // scoring size.
        var (store, _) = Create();

        await store.SaveAsync(store.Read() with { AniListUserName = "hibari" });
        await store.SaveAsync(store.Read() with { ScoringHistorySize = 25 });

        var settings = store.Read();

        Assert.Equal("hibari", settings.AniListUserName);
        Assert.Equal(25, settings.ScoringHistorySize);
    }

    [Fact]
    public void A_value_from_elsewhere_is_reported_as_the_current_one()
    {
        // A page that showed only what the file said would offer to change a value it
        // could not see, and its save would write a second answer beside the first.
        var (store, _) = Create([new("Sync:AniList:UserName", "from-the-environment")]);

        Assert.Equal("from-the-environment", store.Read().AniListUserName);
    }

    [Fact]
    public async Task Rewriting_a_broken_file_clears_the_banner_that_described_it()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{ this is not json");

        var (store, _) = Create();

        var saved = await store.SaveAsync(UserSettings.Defaults with { ScoringHistorySize = 42 });

        // The person who just fixed it has no way to tell a stale warning from a live
        // one, so a reload decides rather than a memory of the failed load.
        Assert.True(saved.Saved);
        Assert.Equal(42, store.Read().ScoringHistorySize);
    }

    [Fact]
    public async Task A_directory_that_cannot_be_written_is_reported_rather_than_thrown()
    {
        // §9's non-root container against a root-owned bind mount. A save button that
        // throws there turns a settings edit into an error page; this makes it a
        // sentence beside the control.
        Directory.CreateDirectory(_directory);

        // A file where the directory should be, which fails a write on every platform
        // without needing permissions a test cannot portably arrange.
        var blocked = Path.Combine(_directory, "blocked");
        await File.WriteAllTextAsync(blocked, string.Empty);

        var status = new UserConfigStatus { Path = Path.Combine(blocked, UserConfigStatus.FileName) };
        var store = new UserSettingsStore(
            new ConfigurationBuilder().Build(),
            status,
            NullLogger<UserSettingsStore>.Instance);

        var result = await store.SaveAsync(UserSettings.Defaults with { ScoringHistorySize = 10 });

        Assert.False(result.Saved);
        Assert.NotNull(result.Error);
        Assert.False(await store.EnsureExistsAsync());
    }

    [Fact]
    public async Task A_written_file_leaves_no_temporary_behind()
    {
        // The write goes through a rename so the file is only ever wholly old or wholly
        // new. A leftover .tmp beside it in /data would be visible to the operator and
        // would look like a crash.
        var (store, _) = Create();

        await store.SaveAsync(UserSettings.Defaults with { ScoringReturnTop = 50 });

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that outlives a test run is not worth failing over.
        }
    }
}

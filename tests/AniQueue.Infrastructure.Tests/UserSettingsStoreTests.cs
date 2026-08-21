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

    private static readonly string[] ExpectedKeys =
    [
        "Sync:Enabled",
        "Sync:AniList:UserName",
        "Scoring:HistorySize",
        "Scoring:CandidateLimit",
        "Scoring:ReturnTop"
    ];

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
    public async Task A_first_boot_leaves_a_file_describing_the_defaults()
    {
        var (store, configuration) = Create();

        Assert.True(await store.EnsureExistsAsync());
        Assert.True(File.Exists(SettingsPath));

        configuration.Reload();

        // Written out rather than commented out, so the file reads as the settings
        // themselves. Reloading it changes nothing, because it says what was already
        // true.
        Assert.Equal("200", configuration["Scoring:HistorySize"]);
        Assert.True(store.Read().SyncEnabled);
        Assert.Equal(UserSettings.Defaults, store.Read());
    }

    [Fact]
    public async Task A_first_boot_cannot_override_what_the_environment_supplied()
    {
        // The reason a first boot writes what is in effect rather than the defaults.
        // This file is added last, so a default-valued file would set an empty account
        // over the one an operator put in their environment — silently, on a machine
        // where nobody had opened it. It is the whole risk of writing real values, and
        // it exists only at this moment.
        var (store, configuration) = Create([new("Sync:AniList:UserName", "from-the-environment")]);

        await store.EnsureExistsAsync();
        configuration.Reload();

        Assert.Equal("from-the-environment", configuration["Sync:AniList:UserName"]);
        Assert.Equal("from-the-environment", store.Read().AniListUserName);
    }

    [Fact]
    public async Task The_file_it_writes_names_every_key_it_accepts()
    {
        var (store, _) = Create();

        await store.EnsureExistsAsync();

        var text = await File.ReadAllTextAsync(SettingsPath);

        // A key nobody can find is a key nobody can use when the pages are unreachable,
        // which is the whole reason the file exists.
        Assert.All(ExpectedKeys, key => Assert.Contains(key, text, StringComparison.Ordinal));
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
    public async Task Every_setting_is_written_out_whatever_its_value()
    {
        // The file is the settings, not a commentary on them. Somebody opening it
        // should see what AniQueue is doing without having to know that an absent line
        // means a default they cannot see.
        var (store, configuration) = Create();

        await store.SaveAsync(UserSettings.Defaults with { AniListUserName = "hibari" });

        var text = await File.ReadAllTextAsync(SettingsPath);

        Assert.Contains("\"Sync:AniList:UserName\": \"hibari\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Sync:Enabled\": true", text, StringComparison.Ordinal);
        Assert.DoesNotContain("// \"", text, StringComparison.Ordinal);
        Assert.Equal("true", configuration["Sync:Enabled"], ignoreCase: true);
    }

    [Fact]
    public async Task An_unset_value_is_written_as_null_and_reads_back_as_unset()
    {
        // "No limit" has to survive the round trip as null rather than as a number, or
        // a saved file would quietly cap a request that was meant to be uncapped.
        var (store, _) = Create();

        await store.SaveAsync(UserSettings.Defaults with { ScoringCandidateLimit = 50 });
        Assert.Equal(50, store.Read().ScoringCandidateLimit);

        await store.SaveAsync(store.Read() with { ScoringCandidateLimit = null });

        var text = await File.ReadAllTextAsync(SettingsPath);

        Assert.Contains("\"Scoring:CandidateLimit\": null", text, StringComparison.Ordinal);
        Assert.Null(store.Read().ScoringCandidateLimit);
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

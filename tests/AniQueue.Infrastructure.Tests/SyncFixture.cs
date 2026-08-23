using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Settings;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Import;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Queue;
using AniQueue.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The sync service wired to a real database with the network replaced by canned
/// responses, shared by the suites that exercise it: the on-demand path, and the
/// unattended one.
///
/// Extracted rather than duplicated because both suites must be looking at exactly
/// the same object graph. The claim under test is that an unattended run is the
/// same pipeline with a different trigger, and two fixtures assembling it slightly
/// differently is how that claim quietly stops being true.
/// </summary>
internal sealed class SyncFixture : IAsyncDisposable
{
    public required SqliteTestDatabase Database { get; init; }

    public required StubAniListClient Client { get; init; }

    public required ISyncService Service { get; init; }

    /// <summary>
    /// The configuration both services read, live.
    /// </summary>
    /// <remarks>
    /// Mutable because the settings sync reads stopped being database rows in Phase
    /// 10a. A test that used to arrange a scenario by inserting a row now changes
    /// this, and a test that exercises <c>SaveSettingsAsync</c> sees the write arrive
    /// here through <see cref="Settings"/> — which means the round trip through the
    /// settings document is covered rather than mocked past.
    /// </remarks>
    public required SyncOptions Options { get; init; }

    public required FakeUserSettingsStore Settings { get; init; }

    public static async Task<SyncFixture> CreateAsync(
        StubAniListClient client,
        SyncOptions? options = null)
    {
        var database = await SqliteTestDatabase.CreateAsync();

        await using (var context = database.CreateContext())
        {
            // A run belongs to a profile, and the default one is created by the
            // initialiser in production.
            context.Profiles.Add(new Profile
            {
                Id = Profile.DefaultProfileId,
                Name = "Test",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await context.SaveChangesAsync();
        }

        var current = options ?? Configured();
        var monitor = new StubOptionsMonitor(current);
        var settings = new FakeUserSettingsStore(current);

        var importService = new ImportService(
            database.ContextFactory,
            new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance),
            monitor,
            NullLogger<ImportService>.Instance);

        return new SyncFixture
        {
            Database = database,
            Client = client,
            Options = current,
            Settings = settings,
            Service = new SyncService(
                database.ContextFactory,
                client,
                new AniListJsonParser(),
                importService,
                monitor,
                settings,
                NullLogger<SyncService>.Instance)
        };
    }

    public static SyncOptions Configured(bool enabled = true) =>
        new() { Enabled = enabled, AniList = new AniListSyncOptions { UserName = "someone" } };

    /// <summary>One AniList entry, in the shape the real response has.</summary>
    public static string Response(
        int id,
        string romaji,
        string? english = null,
        string status = "PLANNING",
        int score = 0,
        int progress = 0) =>
        ListResponse([new AniListEntry(id, romaji, english, status, score, progress)]);

    /// <summary>Several entries in one response, for the cases that need a list.</summary>
    /// <remarks>
    /// Absence is the reason this exists: it can only be observed as the difference
    /// between two fetches, so a test for it needs a response that drops one entry
    /// and keeps another.
    /// </remarks>
    public static string ListResponse(IReadOnlyList<AniListEntry> entries) =>
        $$"""
          {
            "data": { "MediaListCollection": { "hasNextChunk": false, "lists": [
              { "name": "list", "isCustomList": false, "entries": [
                {{string.Join(",\n", entries.Select(EntryJson))}}
              ] }
            ] } }
          }
          """;

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    public async Task<SyncRun?> LastRunAsync()
    {
        await using var context = Database.CreateContext();
        return await context.SyncRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    private static string EntryJson(AniListEntry entry) =>
        $$"""
          {
            "status": "{{entry.Status}}",
            "score": {{entry.Score}},
            "progress": {{entry.Progress}},
            "repeat": 0,
            "startedAt": { "year": null, "month": null, "day": null },
            "completedAt": { "year": null, "month": null, "day": null },
            "media": {
              "id": {{entry.Id}},
              "idMal": {{entry.Id + 1000}},
              "type": "ANIME",
              "format": "TV",
              "episodes": 12,
              "duration": 24,
              "seasonYear": 2021,
              "title": {
                "romaji": "{{entry.Romaji}}",
                "english": {{(entry.English is null ? "null" : $"\"{entry.English}\"")}},
                "native": "ネイティブ"
              },
              "coverImage": { "extraLarge": "https://cdn.example.invalid/c.jpg" }
            }
          }
          """;
}

/// <summary>One entry to render into a canned response.</summary>
internal sealed record AniListEntry(
    int Id,
    string Romaji,
    string? English = null,
    string Status = "PLANNING",
    int Score = 0,
    int Progress = 0);

internal sealed class StubAniListClient(params string[] payloads) : IAniListClient
{
    private string[] _payloads = payloads;

    public string? FailWith { get; set; }

    public List<string> RequestedAccounts { get; } = [];

    /// <summary>
    /// Replaces what the next fetch returns, which is how a test says "the user
    /// deleted something from their list since last time".
    /// </summary>
    public void Returns(params string[] next) => _payloads = next;

    public Task<AniListFetch> FetchListAsync(string userName, CancellationToken cancellationToken = default)
    {
        RequestedAccounts.Add(userName);

        return Task.FromResult(FailWith is not null
            ? AniListFetch.Failed(FailWith)
            : new AniListFetch
            {
                Payloads = [.. _payloads.Select(Encoding.UTF8.GetBytes)]
            });
    }

    /// <summary>
    /// Relations are a different subsystem with its own stub; a list test that
    /// reached this would be asking the wrong object.
    /// </summary>
    public Task<AniListRelationsFetch> FetchRelationsAsync(
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This stub answers list fetches only.");
}

/// <summary>
/// An options monitor over one instance, which the fake settings store mutates.
/// </summary>
/// <remarks>
/// Deliberately handing out the same object rather than a snapshot: in production a
/// save reloads the configuration and the monitor's next read sees it, and a test
/// whose save never became visible would pass while the real thing was broken.
/// </remarks>
internal sealed class StubOptionsMonitor(SyncOptions value) : IOptionsMonitor<SyncOptions>
{
    public SyncOptions CurrentValue => value;

    public SyncOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<SyncOptions, string?> listener) => null;
}

/// <summary>
/// <c>userconfig.json</c> without a disk: reads and writes the same
/// <see cref="SyncOptions"/> the services are bound to.
/// </summary>
/// <remarks>
/// A fake rather than a mock, per the suite's convention, and a two-way one on
/// purpose. Projecting a save back onto the options is what the real store achieves
/// by rewriting the file and reloading configuration, so a test that saves a setting
/// and then reads a status is exercising the same round trip a user gets — including
/// the mapping in <c>SaveSettingsAsync</c>, where a forgotten property would
/// otherwise be invisible.
///
/// Only the <c>Sync</c> section is mirrored. Nothing here reads a scoring key, and a
/// fake that carried them would be claiming coverage it does not have.
/// </remarks>
internal sealed class FakeUserSettingsStore(SyncOptions options) : IUserSettingsStore
{
    /// <summary>Set to make every save fail, as an unwritable bind mount does (§9).</summary>
    public string? FailWith { get; set; }

    public int Saves { get; private set; }

    public string Path => "userconfig.json";

    public UserSettings Read() => new()
    {
        SyncEnabled = options.Enabled,
        SyncPrimarySource = options.PrimarySource,
        AniListUserName = options.AniList.UserName,
        AniListEnabled = options.AniList.Enabled,
        AniListSchedule = options.AniList.Schedule,
        AniListApplyUnattended = options.AniList.ApplyUnattended,
        AniListConflictPolicy = options.AniList.ConflictPolicy,
        AniListAbsencePolicy = options.AniList.AbsencePolicy
    };

    public Task<UserSettingsSaveResult> SaveAsync(
        UserSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (FailWith is { } error)
        {
            return Task.FromResult(UserSettingsSaveResult.Failure(Path, error));
        }

        Saves++;

        options.Enabled = settings.SyncEnabled;
        options.PrimarySource = settings.SyncPrimarySource;
        options.AniList.UserName = settings.AniListUserName;
        options.AniList.Enabled = settings.AniListEnabled;
        options.AniList.Schedule = settings.AniListSchedule;
        options.AniList.ApplyUnattended = settings.AniListApplyUnattended;
        options.AniList.ConflictPolicy = settings.AniListConflictPolicy;
        options.AniList.AbsencePolicy = settings.AniListAbsencePolicy;

        return Task.FromResult(UserSettingsSaveResult.Success(Path));
    }

    public Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

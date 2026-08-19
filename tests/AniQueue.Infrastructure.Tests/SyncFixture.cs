using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;
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

        var importService = new ImportService(
            database.ContextFactory,
            new QueueService(database.ContextFactory, NullLogger<QueueService>.Instance),
            NullLogger<ImportService>.Instance);

        return new SyncFixture
        {
            Database = database,
            Client = client,
            Service = new SyncService(
                database.ContextFactory,
                client,
                new AniListJsonParser(),
                importService,
                new StubOptionsMonitor(options ?? Configured()),
                NullLogger<SyncService>.Instance)
        };
    }

    public static SyncOptions Configured(bool enabled = true) =>
        new() { Enabled = enabled, AniList = new AniListAccountOptions { UserName = "someone" } };

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

/// <summary>An options monitor that never changes, which is all these tests need.</summary>
internal sealed class StubOptionsMonitor(SyncOptions value) : IOptionsMonitor<SyncOptions>
{
    public SyncOptions CurrentValue => value;

    public SyncOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<SyncOptions, string?> listener) => null;
}

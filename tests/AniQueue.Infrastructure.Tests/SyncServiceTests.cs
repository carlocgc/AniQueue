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
/// The sync end to end, with the network replaced by a canned response.
///
/// What is worth asserting is not that the import pipeline works — it has its own
/// suite — but that a sync reuses it rather than growing a second one, and that
/// the run record tells the truth about what happened. A stalled sync rendering as
/// "up to date" is the failure this table exists to prevent (§4).
/// </summary>
public class SyncServiceTests
{
    private sealed class StubAniListClient(params string[] payloads) : IAniListClient
    {
        public string? FailWith { get; set; }

        public List<string> RequestedAccounts { get; } = [];

        public Task<AniListFetch> FetchListAsync(string userName, CancellationToken cancellationToken = default)
        {
            RequestedAccounts.Add(userName);

            return Task.FromResult(FailWith is not null
                ? AniListFetch.Failed(FailWith)
                : new AniListFetch
                {
                    Payloads = [.. payloads.Select(Encoding.UTF8.GetBytes)]
                });
        }
    }

    private sealed class SyncFixture : IAsyncDisposable
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

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    /// <summary>An options monitor that never changes, which is all these tests need.</summary>
    private sealed class StubOptionsMonitor(SyncOptions value) : IOptionsMonitor<SyncOptions>
    {
        public SyncOptions CurrentValue => value;

        public SyncOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SyncOptions, string?> listener) => null;
    }

    private static SyncOptions Configured(bool enabled = true) =>
        new() { Enabled = enabled, AniList = new AniListAccountOptions { UserName = "someone" } };

    /// <summary>One AniList entry, in the shape the real response has.</summary>
    private static string Response(
        int id,
        string romaji,
        string? english = null,
        string status = "PLANNING",
        int score = 0,
        int progress = 0) =>
        $$"""
          {
            "data": { "MediaListCollection": { "hasNextChunk": false, "lists": [
              { "name": "{{status}}", "isCustomList": false, "entries": [
                {
                  "status": "{{status}}",
                  "score": {{score}},
                  "progress": {{progress}},
                  "repeat": 0,
                  "startedAt": { "year": null, "month": null, "day": null },
                  "completedAt": { "year": null, "month": null, "day": null },
                  "media": {
                    "id": {{id}},
                    "idMal": {{id + 1000}},
                    "type": "ANIME",
                    "format": "TV",
                    "episodes": 12,
                    "duration": 24,
                    "seasonYear": 2021,
                    "title": {
                      "romaji": "{{romaji}}",
                      "english": {{(english is null ? "null" : $"\"{english}\"")}},
                      "native": "ネイティブ"
                    },
                    "coverImage": { "extraLarge": "https://cdn.example.invalid/c.jpg" }
                  }
                }
              ] }
            ] } }
          }
          """;

    private static async Task<SyncRun?> LastRunAsync(SyncFixture fixture)
    {
        await using var context = fixture.Database.CreateContext();
        return await context.SyncRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task A_fetch_previews_without_writing_anything()
    {
        // The same rule an upload obeys: nothing reaches the database until someone
        // has seen what it would do (D21 makes the preview the review surface).
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.True(fetch.Succeeded);
        Assert.Equal(1, fetch.Preview!.CreateCount);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(0, await context.Anime.CountAsync());
        Assert.Equal(0, await context.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task Applying_a_reviewed_preview_writes_the_library_and_the_run()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        var applied = await fixture.Service.ApplyAsync(
            fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, applied.Commit.Created);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal("Sora no Kakera", anime.Title);
        Assert.Equal(24, anime.EpisodeDurationMinutes);
        Assert.Equal(2021, anime.ReleaseYear);

        var run = await context.SyncRuns.SingleAsync();
        Assert.Equal(SyncOutcome.Succeeded, run.Outcome);
        Assert.Equal(1, run.Created);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task The_bridge_holds_through_a_sync()
    {
        // D17 through the real path rather than the seam: a MyAnimeList-imported row
        // met by an AniList sync is one title, not two. This is the test that stops
        // a first sync turning a 750-entry library into 750 conflicts.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(
                setup, "Sora no Kakera", AnimeSource.MyAnimeList, "901101");

            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            await setup.SaveChangesAsync();
        }

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());
        Assert.Equal(2, await context.AnimeExternalIds.CountAsync());
    }

    [Fact]
    public async Task A_list_that_already_matches_records_a_run_saying_so()
    {
        // Otherwise a sync that found nothing to do is indistinguishable from one
        // that never happened, and the page cannot say when it last worked.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var first = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(first.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        var second = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.True(second.IsComplete);
        Assert.False(second.Preview!.HasApplicableChanges);

        var run = await LastRunAsync(fixture);
        Assert.Equal(SyncOutcome.NothingToDo, run!.Outcome);
    }

    [Fact]
    public async Task A_failed_fetch_is_recorded_with_its_reason()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient { FailWith = "AniList has no such user, or the list is private." });

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);

        var run = await LastRunAsync(fixture);
        Assert.Equal(SyncOutcome.Failed, run!.Outcome);
        Assert.Contains("private", run.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_GraphQL_error_is_a_failed_run_rather_than_an_empty_library()
    {
        // The dangerous misreading, at the level that would act on it. An errors
        // array parsed as zero entries looks exactly like the user having deleted
        // their list, which is the population D19's absence handling touches.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient("""{ "errors": [{ "message": "Too Many Requests" }], "data": null }"""));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);
        Assert.Null(fetch.Preview);

        var run = await LastRunAsync(fixture);
        Assert.Equal(SyncOutcome.Failed, run!.Outcome);
    }

    [Fact]
    public async Task The_kill_switch_stops_the_sync_and_records_nothing()
    {
        // Nothing was attempted, so there is nothing to audit — and a log full of
        // runs that never ran would bury the failures that did (D20).
        var client = new StubAniListClient(Response(900101, "Sora no Kakera"));
        await using var fixture = await SyncFixture.CreateAsync(client, Configured(enabled: false));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);
        Assert.Empty(client.RequestedAccounts);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(0, await context.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task An_unconfigured_account_fails_before_any_request()
    {
        var client = new StubAniListClient(Response(900101, "Sora no Kakera"));
        await using var fixture = await SyncFixture.CreateAsync(client, new SyncOptions());

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);
        Assert.Empty(client.RequestedAccounts);
    }

    [Fact]
    public async Task A_source_switched_off_for_the_profile_does_not_sync()
    {
        var client = new StubAniListClient(Response(900101, "Sora no Kakera"));
        await using var fixture = await SyncFixture.CreateAsync(client);

        await using (var setup = fixture.Database.CreateContext())
        {
            setup.SourceSyncSettings.Add(new SourceSyncSettings
            {
                ProfileId = Profile.DefaultProfileId,
                Source = AnimeSource.AniList,
                IsEnabled = false
            });

            await setup.SaveChangesAsync();
        }

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);
        Assert.Empty(client.RequestedAccounts);
    }

    [Fact]
    public async Task Changing_the_title_language_takes_effect_without_a_sync()
    {
        // The point of storing each title against its language. Before, the setting
        // only landed when a later sync happened to rewrite the row — so a library
        // already up to date could not change language at all without re-fetching
        // the entire list, which is a data migration wearing a preference's clothes
        // (D22).
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera", english: "Fragments of Sky")));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        await using (var before = fixture.Database.CreateContext())
        {
            Assert.Equal("Sora no Kakera", (await before.Anime.SingleAsync()).Title);
        }

        await fixture.Service.SavePreferredTitleLanguageAsync(
            Profile.DefaultProfileId, TitleLanguage.English);

        await using (var after = fixture.Database.CreateContext())
        {
            var anime = await after.Anime.SingleAsync();

            Assert.Equal("Fragments of Sky", anime.Title);

            // The variants are untouched by the switch, which is what lets it go
            // back again.
            Assert.Equal("Sora no Kakera", anime.TitleRomaji);
            Assert.Equal("ネイティブ", anime.TitleNative);
        }

        // And back, with no fetch in between either direction.
        await fixture.Service.SavePreferredTitleLanguageAsync(
            Profile.DefaultProfileId, TitleLanguage.Romaji);

        await using var restored = fixture.Database.CreateContext();
        Assert.Equal("Sora no Kakera", (await restored.Anime.SingleAsync()).Title);

        // One fetch in the whole test: the two language switches asked the source for
        // nothing at all.
        Assert.Single(fixture.Client.RequestedAccounts);
    }

    [Fact]
    public async Task A_title_with_only_one_name_is_left_alone_by_the_switch()
    {
        // Every manual entry and everything from a MyAnimeList export is in this
        // position, which is why the Sources page can promise they are unaffected.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Hand Added", AnimeSource.Manual);
        }

        await fixture.Service.SavePreferredTitleLanguageAsync(
            Profile.DefaultProfileId, TitleLanguage.Native);

        await using var context = fixture.Database.CreateContext();
        var manual = await context.Anime.SingleAsync(a => a.Source == AnimeSource.Manual);

        Assert.Equal("Hand Added", manual.Title);
    }


    [Fact]
    public async Task The_title_preference_decides_which_name_the_library_shows()
    {
        // D22 in one assertion: the same response writes a different Title depending
        // on a setting, and the other variant is kept beside it rather than lost.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera", english: "Fragments of Sky")));

        await using (var setup = fixture.Database.CreateContext())
        {
            setup.ProfileSettings.Add(new ProfileSettings
            {
                ProfileId = Profile.DefaultProfileId,
                DisplayName = "Test",
                PreferredTitleLanguage = TitleLanguage.English
            });

            await setup.SaveChangesAsync();
        }

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal("Fragments of Sky", anime.Title);
        Assert.Equal("Sora no Kakera", anime.TitleRomaji);
        Assert.Equal("Fragments of Sky", anime.TitleEnglish);
    }

    [Fact]
    public async Task A_sync_releases_queue_slots_and_says_how_many()
    {
        // Queue advancement is the import pipeline's (D12), and reusing it is the
        // point — the difference between an upload and a sync is the trigger. The
        // count is on the run record so an unattended one can report it later.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera", status: "COMPLETED", progress: 12)));

        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await SeedData.CreateAnimeAsync(
                setup, "Sora no Kakera", AnimeSource.AniList, "900101");

            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            setup.QueueItems.Add(SeedData.QueueSlot(Profile.DefaultProfileId, 0, anime.Id));
            await setup.SaveChangesAsync();
        }

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        var applied = await fixture.Service.ApplyAsync(
            fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, applied.Commit.QueueSlotsReleased);

        var run = await LastRunAsync(fixture);
        Assert.Equal(1, run!.SlotsReleased);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(0, await context.QueueItems.CountAsync());
    }

    [Fact]
    public async Task Held_conflicts_are_counted_on_the_run()
    {
        // The pending-decision count the Sources page badges. A conflict left
        // unresolved is not a skip that finished — it comes back next sync.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await using (var setup = fixture.Database.CreateContext())
        {
            // Hand-added, so it carries no identifier and the incoming entry meets a
            // same-titled row it cannot confidently claim.
            var anime = await SeedData.CreateAnimeAsync(setup, "Sora no Kakera");
            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            await setup.SaveChangesAsync();
        }

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, fetch.Preview!.ConflictCount);

        // A preview holding a decision is not a finished run, so nothing is recorded
        // until the user acts on it.
        Assert.False(fetch.IsComplete);
        Assert.Null(await LastRunAsync(fixture));

        var applied = await fixture.Service.ApplyAsync(
            fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, applied.ConflictsHeld);

        var run = await LastRunAsync(fixture);
        Assert.Equal(1, run!.ConflictsHeld);
        Assert.Equal(SyncOutcome.NothingToDo, run.Outcome);
    }

    [Fact]
    public async Task Status_reports_the_account_and_the_most_recent_run()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var before = Assert.Single(await fixture.Service.GetStatusAsync(Profile.DefaultProfileId));
        Assert.True(before.IsConfigured);
        Assert.Equal("someone", before.Account);
        Assert.Null(before.LastRun);

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(fetch.Preview!, Profile.DefaultProfileId, AnimeSource.AniList);

        var after = Assert.Single(await fixture.Service.GetStatusAsync(Profile.DefaultProfileId));
        Assert.Equal(SyncOutcome.Succeeded, after.LastRun!.Outcome);
    }

    [Fact]
    public async Task Settings_are_created_on_first_save_and_updated_after()
    {
        // The Sources page writes each control as it is changed, so this runs on
        // every click. The first one has no row to update.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var status = Assert.Single(await fixture.Service.GetStatusAsync(Profile.DefaultProfileId));

        status.Settings.PrecedenceRank = 1;
        status.Settings.AbsencePolicy = SyncAbsencePolicy.Ignore;
        await fixture.Service.SaveSettingsAsync(status.Settings);

        var afterCreate = Assert.Single(await fixture.Service.GetStatusAsync(Profile.DefaultProfileId));
        Assert.Equal(1, afterCreate.Settings.PrecedenceRank);

        afterCreate.Settings.PrecedenceRank = 0;
        await fixture.Service.SaveSettingsAsync(afterCreate.Settings);

        await using var context = fixture.Database.CreateContext();
        var stored = await context.SourceSyncSettings.SingleAsync();

        Assert.Equal(0, stored.PrecedenceRank);
        Assert.Equal(SyncAbsencePolicy.Ignore, stored.AbsencePolicy);
    }

    [Fact]
    public async Task The_title_preference_survives_a_profile_with_no_settings_row()
    {
        // Nothing creates a ProfileSettings row for the default profile today, so
        // the first person to touch this control would otherwise hit a null.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        Assert.Equal(
            TitleLanguage.Romaji,
            await fixture.Service.GetPreferredTitleLanguageAsync(Profile.DefaultProfileId));

        await fixture.Service.SavePreferredTitleLanguageAsync(
            Profile.DefaultProfileId, TitleLanguage.Native);

        Assert.Equal(
            TitleLanguage.Native,
            await fixture.Service.GetPreferredTitleLanguageAsync(Profile.DefaultProfileId));
    }

    [Fact]
    public async Task Only_a_source_with_a_list_to_fetch_can_be_synced()
    {
        // MyAnimeList is a file import. Asking to sync it is a programming error,
        // not something to report to a user, because nothing offers it.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.MyAnimeList));
    }
}

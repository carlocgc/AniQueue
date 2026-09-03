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
using static AniQueue.Infrastructure.Tests.SyncFixture;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The sync end to end, with the network replaced by a canned response.
///
/// What is worth asserting is not that the import pipeline works — it has its own
/// suite — but that a sync reuses it rather than growing a second one, and that
/// the run record tells the truth about what happened. A stalled sync rendering as
/// "up to date" is the failure this table exists to prevent.
/// </summary>
public class SyncServiceTests
{

    [Fact]
    public async Task A_fetch_previews_without_writing_anything()
    {
        // The same rule an upload obeys: nothing reaches the database until someone
        // has seen what it would do.
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
            fetch, Profile.DefaultProfileId);

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
        // The identifier bridge through the real path rather than the seam: a MyAnimeList-imported row
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
        await fixture.Service.ApplyAsync(fetch, Profile.DefaultProfileId);

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
        await fixture.Service.ApplyAsync(first, Profile.DefaultProfileId);

        var second = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.True(second.IsComplete);
        Assert.False(second.Preview!.HasApplicableChanges);

        var run = await fixture.LastRunAsync();
        Assert.Equal(SyncOutcome.NothingToDo, run!.Outcome);
    }

    [Fact]
    public async Task A_failed_fetch_is_recorded_with_its_reason()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient { FailWith = "AniList has no such user, or the list is private." });

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);

        var run = await fixture.LastRunAsync();
        Assert.Equal(SyncOutcome.Failed, run!.Outcome);
        Assert.Contains("private", run.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_GraphQL_error_is_a_failed_run_rather_than_an_empty_library()
    {
        // The dangerous misreading, at the level that would act on it. An errors
        // array parsed as zero entries looks exactly like the user having deleted
        // their list, which is the population the absence handling touches.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient("""{ "errors": [{ "message": "Too Many Requests" }], "data": null }"""));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.False(fetch.Succeeded);
        Assert.Null(fetch.Preview);

        var run = await fixture.LastRunAsync();
        Assert.Equal(SyncOutcome.Failed, run!.Outcome);
    }

    [Fact]
    public async Task The_kill_switch_stops_the_sync_and_records_nothing()
    {
        // Nothing was attempted, so there is nothing to audit — and a log full of
        // runs that never ran would bury the failures that did.
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

        fixture.Options.AniList.Enabled = false;

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
        // the entire list, which is a data migration wearing a preference's clothes.

        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera", english: "Fragments of Sky")));

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(fetch, Profile.DefaultProfileId);

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
        // The title preference in one assertion: the same response writes a different Title depending
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
        await fixture.Service.ApplyAsync(fetch, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();

        Assert.Equal("Fragments of Sky", anime.Title);
        Assert.Equal("Sora no Kakera", anime.TitleRomaji);
        Assert.Equal("Fragments of Sky", anime.TitleEnglish);
    }

    [Fact]
    public async Task A_sync_releases_queue_slots_and_says_how_many()
    {
        // Queue advancement is the import pipeline's, and reusing it is the
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
            fetch, Profile.DefaultProfileId);

        Assert.Equal(1, applied.Commit.QueueSlotsReleased);

        var run = await fixture.LastRunAsync();
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
        Assert.Null(await fixture.LastRunAsync());

        var applied = await fixture.Service.ApplyAsync(
            fetch, Profile.DefaultProfileId);

        Assert.Equal(1, applied.ConflictsHeld);

        var run = await fixture.LastRunAsync();
        Assert.Equal(1, run!.ConflictsHeld);
        Assert.Equal(SyncOutcome.NothingToDo, run.Outcome);
    }

    [Fact]
    public async Task Status_reports_the_account_and_the_most_recent_run()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var before = await AniListStatusAsync(fixture);
        Assert.True(before.IsConfigured);
        Assert.Equal("someone", before.Account);
        Assert.Null(before.LastRun);

        var fetch = await fixture.Service.FetchAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await fixture.Service.ApplyAsync(fetch, Profile.DefaultProfileId);

        var after = await AniListStatusAsync(fixture);
        Assert.Equal(SyncOutcome.Succeeded, after.LastRun!.Outcome);
    }

    /// <summary>
    /// Every setting a save carries comes back on the next read.
    /// </summary>
    /// <remarks>
    /// The hazard this guards is
    /// an update that copied the entity field by field, where a property missing from
    /// the copy worked exactly once — the first save wrote the whole row — and then
    /// silently stopped. It is now a mapping from <c>SourceSyncSettings</c> into
    /// <c>UserSettings</c>, where a property missing from the mapping never saves at
    /// all. Both fail the same way from the user's chair: a control that moves, says
    /// "Saved", and does nothing.
    ///
    /// Saved twice on purpose. The first save is what a fresh install does; the second
    /// is what every subsequent click does, and only the second could catch the old
    /// bug. Keeping both costs a line and keeps the test honest about which it means.
    /// </remarks>
    [Fact]
    public async Task Every_setting_comes_back_from_a_save()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var first = await AniListStatusAsync(fixture);
        await fixture.Service.SaveSettingsAsync(first.Settings);

        var second = await AniListStatusAsync(fixture);

        // Every field moved off its default, in one save.
        await fixture.Service.SaveSettingsAsync(second.Settings with
        {
            IsEnabled = false,
            ApplyUnattended = false,
            ConflictPolicy = SyncConflictPolicy.LinkToExisting,
            AbsencePolicy = SyncAbsencePolicy.Ignore
        });

        var stored = (await AniListStatusAsync(fixture)).Settings;

        Assert.False(stored.IsEnabled);
        Assert.False(stored.ApplyUnattended);
        Assert.Equal(SyncConflictPolicy.LinkToExisting, stored.ConflictPolicy);
        Assert.Equal(SyncAbsencePolicy.Ignore, stored.AbsencePolicy);
    }

    /// <summary>
    /// A save that cannot reach the file says so rather than reporting success.
    /// </summary>
    /// <remarks>
    /// A non-root container writing to a root-owned bind mount is a real
    /// deployment, and these settings live in a file. A toggle that
    /// reported "Saved" over a failed write would show a value nothing kept.
    /// </remarks>
    [Fact]
    public async Task A_settings_save_that_cannot_write_reports_the_failure()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        fixture.Settings.FailWith = "Permission denied";

        var status = await AniListStatusAsync(fixture);
        var result = await fixture.Service.SaveSettingsAsync(
            status.Settings with { AbsencePolicy = SyncAbsencePolicy.Ignore });

        Assert.False(result.Saved);
        Assert.Equal("Permission denied", result.Error);
        Assert.Equal(
            SyncAbsencePolicy.Flag,
            (await AniListStatusAsync(fixture)).Settings.AbsencePolicy);
    }

    /// <summary>
    /// MyAnimeList has no settings to save, because nothing runs on its behalf.
    /// </summary>
    /// <remarks>
    /// Every value here describes something a run does,
    /// and the file has no MyAnimeList section to write them to — so a save is a
    /// programming error rather than a no-op that looks like it worked.
    /// </remarks>
    [Fact]
    public async Task A_file_source_has_no_run_settings_to_save()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.SaveSettingsAsync(
                SourceSyncSettings.DefaultsFor(AnimeSource.MyAnimeList)));
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

    // --- Sources, and which one is primary -------------------------

    /// <summary>
    /// Every source the page accounts for is reported, whether or not anything can
    /// be fetched from it — which is what gives MyAnimeList somewhere to be ranked.
    /// </summary>
    [Fact]
    public async Task Status_reports_the_file_source_as_well_as_the_syncing_one()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        var statuses = await fixture.Service.GetStatusAsync(Profile.DefaultProfileId);

        Assert.Equal(
            [AnimeSource.AniList, AnimeSource.MyAnimeList],
            statuses.Select(s => s.Source));

        Assert.True(statuses.Single(s => s.Source == AnimeSource.AniList).CanFetch);

        // Nothing to fetch, and therefore nothing to configure an account for.
        var file = statuses.Single(s => s.Source == AnimeSource.MyAnimeList);
        Assert.False(file.CanFetch);
        Assert.True(file.IsConfigured);
        Assert.Null(file.Account);
    }

    /// <summary>
    /// Before anybody chooses, AniList holds the seat and exactly one source does.
    /// </summary>
    /// <remarks>
    /// An empty seat is the tie — two sources both describing a title, resolved by
    /// letting the last import win — which is the behaviour the setting exists to
    /// end, so there is no state where nobody holds it.
    /// </remarks>
    [Fact]
    public async Task AniList_holds_the_primary_seat_before_anybody_chooses()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        var statuses = await fixture.Service.GetStatusAsync(Profile.DefaultProfileId);

        Assert.Equal(AnimeSource.AniList, Assert.Single(statuses, s => s.IsPrimary).Source);
    }

    [Fact]
    public async Task Promoting_a_source_demotes_every_other_one()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        await fixture.Service.SetPrimarySourceAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var afterFirst = await fixture.Service.GetStatusAsync(Profile.DefaultProfileId);
        Assert.Equal(AnimeSource.AniList, afterFirst.Single(s => s.IsPrimary).Source);

        // The seat is single: promoting the other has to move it, not share it.
        await fixture.Service.SetPrimarySourceAsync(Profile.DefaultProfileId, AnimeSource.MyAnimeList);

        var afterSecond = await fixture.Service.GetStatusAsync(Profile.DefaultProfileId);
        Assert.Equal(AnimeSource.MyAnimeList, afterSecond.Single(s => s.IsPrimary).Source);
    }

    /// <summary>
    /// Promoting one source demotes the other, and cannot fail to.
    /// </summary>
    /// <remarks>
    /// The seat is one key naming its occupant, so the demotion is not a write that
    /// could be missed — it is the same value read the other way round. What is worth
    /// asserting is that exactly one source claims it.
    /// </remarks>
    [Fact]
    public async Task Promoting_one_source_demotes_the_other()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        // Starting from the other source, so the promotion below has something to move
        // rather than confirming the default it was already sitting on.
        await fixture.Service.SetPrimarySourceAsync(Profile.DefaultProfileId, AnimeSource.MyAnimeList);

        var before = await fixture.Service.GetStatusAsync(Profile.DefaultProfileId);
        Assert.Equal(AnimeSource.MyAnimeList, Assert.Single(before, s => s.IsPrimary).Source);

        await fixture.Service.SetPrimarySourceAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var after = await fixture.Service.GetStatusAsync(Profile.DefaultProfileId);

        Assert.Single(after, s => s.IsPrimary);
        Assert.True(after.Single(s => s.Source == AnimeSource.AniList).IsPrimary);
        Assert.False(after.Single(s => s.Source == AnimeSource.MyAnimeList).IsPrimary);
    }

    /// <summary>
    /// Promotion leaves everything else about a source alone — it is one decision,
    /// not a reset of the card it sits on.
    /// </summary>
    [Fact]
    public async Task Promoting_a_source_does_not_disturb_its_other_settings()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        await fixture.Service.SaveSettingsAsync(new SourceSyncSettings
        {
            Source = AnimeSource.AniList,
            AbsencePolicy = SyncAbsencePolicy.Ignore,
            IsEnabled = false
        });

        await fixture.Service.SetPrimarySourceAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var status = await AniListStatusAsync(fixture);

        Assert.True(status.IsPrimary);
        Assert.Equal(SyncAbsencePolicy.Ignore, status.Settings.AbsencePolicy);
        Assert.False(status.Settings.IsEnabled);
    }

    [Fact]
    public async Task A_manual_title_has_no_settings_to_promote()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.SetPrimarySourceAsync(Profile.DefaultProfileId, AnimeSource.Manual));
    }

    /// <summary>
    /// Keeping a title has to outlast the fetch that asked about it.
    /// </summary>
    /// <remarks>
    /// The mark records what the source said, so clearing it as the answer would let
    /// the very next fetch write it straight back and ask again — the decision would
    /// survive until the next sync and no longer.
    /// </remarks>
    [Fact]
    public async Task A_title_the_user_keeps_is_not_asked_about_again()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var absent = Assert.Single((await AniListStatusAsync(fixture)).AbsentTitles);

        var resolved = await fixture.Service.ResolveAbsenceAsync(
            Profile.DefaultProfileId, AnimeSource.AniList, [absent.AnimeId], AbsenceResolution.Keep);

        Assert.Equal(1, resolved);
        Assert.Equal(0, (await AniListStatusAsync(fixture)).AbsentCount);

        // The title itself is untouched — keeping is an answer, not a change.
        await using (var context = fixture.Database.CreateContext())
        {
            Assert.Equal(2, await context.LibraryEntries.CountAsync());
        }

        // And the next fetch, which still does not list it, does not reopen it.
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(0, (await AniListStatusAsync(fixture)).AbsentCount);
        Assert.Equal(0, await fixture.Service.CountUnresolvedAbsencesAsync(Profile.DefaultProfileId));
    }

    [Fact]
    public async Task A_kept_title_is_asked_about_again_if_it_leaves_a_second_time()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var absent = Assert.Single((await AniListStatusAsync(fixture)).AbsentTitles);
        await fixture.Service.ResolveAbsenceAsync(
            Profile.DefaultProfileId, AnimeSource.AniList, [absent.AnimeId], AbsenceResolution.Keep);

        // Listed again clears the answer along with the mark, because the question it
        // answered is no longer the one being asked.
        fixture.Client.Returns(TwoTitles());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, (await AniListStatusAsync(fixture)).AbsentCount);
    }

    [Fact]
    public async Task Deleting_an_absence_by_hand_removes_the_entry_and_its_slot()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var absent = Assert.Single((await AniListStatusAsync(fixture)).AbsentTitles);

        await using (var setup = fixture.Database.CreateContext())
        {
            setup.QueueItems.Add(SeedData.QueueSlot(Profile.DefaultProfileId, 0, absent.AnimeId));
            await setup.SaveChangesAsync();
        }

        var resolved = await fixture.Service.ResolveAbsenceAsync(
            Profile.DefaultProfileId,
            AnimeSource.AniList,
            [absent.AnimeId],
            AbsenceResolution.Delete);

        Assert.Equal(1, resolved);

        await using var context = fixture.Database.CreateContext();

        Assert.Equal(1, await context.LibraryEntries.CountAsync());
        Assert.False(await context.QueueItems.AnyAsync());

        // The catalogue row survives, because relation edges and recommendation
        // history point at it.
        Assert.Equal(2, await context.Anime.CountAsync());
    }

    /// <summary>
    /// Queue position is contiguous by invariant rather than by constraint.
    /// </summary>
    /// <remarks>
    /// Nothing in the schema enforces it — position deliberately carries no unique
    /// index, because SQLite checks uniqueness per statement and any reorder shifting
    /// a block of rows would collide mid-transaction. So a delete that leaves a hole
    /// leaves the next reorder computing indices against a sequence with one.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_queued_title_closes_the_gap_it_leaves()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var absent = Assert.Single((await AniListStatusAsync(fixture)).AbsentTitles);
        var staying = await AnimeIdForAsync(fixture, "900101");

        await using (var setup = fixture.Database.CreateContext())
        {
            // The one leaving sits between two that stay, so removing it opens a gap
            // rather than shortening the tail.
            var extra = await SeedData.CreateAnimeAsync(setup, "Kaze no Tani");
            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, extra.Id));
            setup.QueueItems.Add(SeedData.QueueSlot(Profile.DefaultProfileId, 0, staying));
            setup.QueueItems.Add(SeedData.QueueSlot(Profile.DefaultProfileId, 1, absent.AnimeId));
            setup.QueueItems.Add(SeedData.QueueSlot(Profile.DefaultProfileId, 2, extra.Id));
            await setup.SaveChangesAsync();
        }

        await fixture.Service.ResolveAbsenceAsync(
            Profile.DefaultProfileId,
            AnimeSource.AniList,
            [absent.AnimeId],
            AbsenceResolution.Delete);

        await using var context = fixture.Database.CreateContext();

        var positions = await context.QueueItems
            .Where(q => q.ProfileId == Profile.DefaultProfileId)
            .OrderBy(q => q.Position)
            .Select(q => q.Position)
            .ToListAsync();

        Assert.Equal([0, 1], positions);
    }

    /// <summary>
    /// A page's list is as old as its last load, and a sync may have run since.
    /// </summary>
    [Fact]
    public async Task Answering_an_absence_the_source_has_taken_back_does_nothing()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var absent = Assert.Single((await AniListStatusAsync(fixture)).AbsentTitles);

        // The source starts listing it again between the page loading and the click.
        fixture.Client.Returns(TwoTitles());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var resolved = await fixture.Service.ResolveAbsenceAsync(
            Profile.DefaultProfileId,
            AnimeSource.AniList,
            [absent.AnimeId],
            AbsenceResolution.Delete);

        Assert.Equal(0, resolved);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(2, await context.LibraryEntries.CountAsync());
    }

    private static string TwoTitles() => ListResponse(
        [new AniListEntry(900101, "Sora no Kakera"), new AniListEntry(900102, "Yoru no Hate")]);

    private static string OneTitle() => ListResponse([new AniListEntry(900101, "Sora no Kakera")]);

    /// <summary>Which catalogue row a sync gave one AniList identifier.</summary>
    private static async Task<int> AnimeIdForAsync(SyncFixture fixture, string externalId)
    {
        await using var context = fixture.Database.CreateContext();

        return await context.AnimeExternalIds
            .Where(x => x.Source == AnimeSource.AniList && x.ExternalId == externalId)
            .Select(x => x.AnimeId)
            .SingleAsync();
    }

    /// <summary>
    /// The AniList status specifically, because the page now accounts for every
    /// source rather than only the ones something can be fetched from.
    /// </summary>
    private static async Task<SourceSyncStatus> AniListStatusAsync(SyncFixture fixture) =>
        (await fixture.Service.GetStatusAsync(Profile.DefaultProfileId))
            .Single(s => s.Source == AnimeSource.AniList);
}

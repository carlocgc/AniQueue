using AniQueue.Core.Domain;
using AniQueue.Core.Sync;
using Microsoft.EntityFrameworkCore;
using static AniQueue.Infrastructure.Tests.SyncFixture;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// A sync with nobody watching.
///
/// Everything covered here happens while the user is asleep: what an unattended
/// run may apply, what it must hold, and what it is entitled to conclude from a
/// title's absence. The last of those has more tests than behaviour, because a
/// wrong answer to it is the one failure in the product with no recovery path.
/// </summary>
public class UnattendedSyncTests
{
    /// <summary>
    /// Changes one setting the way the Sources page does — through a real save.
    /// </summary>
    /// <remarks>
    /// Deliberately still going through <c>SaveSettingsAsync</c> rather than poking
    /// the options directly, even though the latter is possible. These
    /// tests are about what an unattended run does with a setting the user changed,
    /// and a helper that skipped the save would stop covering the path the user takes
    /// to get there.
    /// </remarks>
    private static async Task ConfigureAsync(
        SyncFixture fixture,
        Func<SourceSyncSettings, SourceSyncSettings> configure)
    {
        var status = await AniListStatusAsync(fixture);
        await fixture.Service.SaveSettingsAsync(configure(status.Settings));
    }

    private static string TwoTitles() => ListResponse(
        [new AniListEntry(900101, "Sora no Kakera"), new AniListEntry(900102, "Yoru no Hate")]);

    private static string OneTitle() => ListResponse([new AniListEntry(900101, "Sora no Kakera")]);

    [Fact]
    public async Task An_unattended_run_applies_the_unambiguous()
    {
        // The safe subset is the preview with conflicts withheld, and nothing
        // here re-decides what "safe" means.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.Created);
        Assert.True(result.ChangedLibrary);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());

        var run = await context.SyncRuns.SingleAsync();
        Assert.Equal(SyncOutcome.Succeeded, run.Outcome);
        Assert.Equal(1, run.Created);
    }

    [Fact]
    public async Task A_source_set_to_ask_first_writes_nothing_and_says_so()
    {
        // What the fourth outcome exists for: a run that found changes and applied
        // none of them is not a run that found nothing, and a page unable to tell
        // them apart reports a stalled library as up to date.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await ConfigureAsync(fixture, s => s with { ApplyUnattended = false });

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.HeldForReview, result.Outcome);
        Assert.Equal(1, result.ChangesHeld);
        Assert.False(result.ChangedLibrary);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(0, await context.Anime.CountAsync());

        var run = await context.SyncRuns.SingleAsync();
        Assert.Equal(SyncOutcome.HeldForReview, run.Outcome);
        Assert.Equal(1, run.ChangesHeld);
    }

    [Fact]
    public async Task Conflicts_are_held_unless_the_user_opted_into_linking()
    {
        // Hold for review is the default: a match the
        // application cannot confirm is never merged without a person.
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

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.HeldForReview, result.Outcome);
        Assert.Equal(1, result.ConflictsHeld);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(0, await context.AnimeExternalIds.CountAsync());
    }

    [Fact]
    public async Task Linking_by_exact_title_converges()
    {
        // The only resolution that may be automated, because writing the identifier
        // is what stops the entry conflicting again on every subsequent run.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await using (var setup = fixture.Database.CreateContext())
        {
            // Cased differently on purpose: the test is exact equality ignoring case,
            // not a similarity heuristic.
            var anime = await SeedData.CreateAnimeAsync(setup, "sora no kakera");
            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            await setup.SaveChangesAsync();
        }

        await ConfigureAsync(fixture, s => s with { ConflictPolicy = SyncConflictPolicy.LinkToExisting });

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.Succeeded, result.Outcome);

        await using (var context = fixture.Database.CreateContext())
        {
            // One title, now carrying the identifier — not a second copy of it.
            Assert.Equal(1, await context.Anime.CountAsync());
            Assert.True(await context.AnimeExternalIds.AnyAsync(
                x => x.Source == AnimeSource.AniList && x.ExternalId == "900101"));
        }

        // And the next run has nothing left to conflict about, which is the whole
        // reason this resolution is preferred to skipping.
        var second = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(0, second.ConflictsHeld);
    }

    [Fact]
    public async Task An_ambiguous_conflict_is_never_linked()
    {
        // Two local rows with the same name produce a conflict carrying no candidate
        // at all, so there is nothing for the policy to act on even where the user
        // has opted into linking.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await using (var setup = fixture.Database.CreateContext())
        {
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var anime = await SeedData.CreateAnimeAsync(setup, "Sora no Kakera");
                setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, anime.Id));
            }

            await setup.SaveChangesAsync();
        }

        await ConfigureAsync(fixture, s => s with { ConflictPolicy = SyncConflictPolicy.LinkToExisting });

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.HeldForReview, result.Outcome);
        Assert.Equal(1, result.ConflictsHeld);
        Assert.Equal(0, result.Created);
    }

    [Fact]
    public async Task A_title_the_source_stopped_listing_is_flagged()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        // The user deletes one of them from their list.
        fixture.Client.Returns(OneTitle());

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, result.AbsentFlagged);

        await using var context = fixture.Database.CreateContext();

        var flagged = await context.AnimeExternalIds
            .Where(x => x.Source == AnimeSource.AniList && x.MissingFromSourceAt != null)
            .ToListAsync();

        Assert.Equal("900102", Assert.Single(flagged).ExternalId);

        // Flagged, and nothing more. Deleting is a different policy, and this one
        // says only that the source stopped listing it.
        Assert.Equal(2, await context.LibraryEntries.CountAsync());

        var status = await AniListStatusAsync(fixture);
        Assert.Equal(1, status.AbsentCount);
        Assert.Equal("Yoru no Hate", Assert.Single(status.AbsentTitles).Title);
    }

    [Fact]
    public async Task A_title_the_source_lists_again_is_unflagged()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(OneTitle());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(TwoTitles());
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        await using var context = fixture.Database.CreateContext();

        // The mark describes the most recent fetch rather than accumulating history,
        // so a list the user puts back clears without anyone intervening.
        Assert.False(await context.AnimeExternalIds.AnyAsync(x => x.MissingFromSourceAt != null));
    }

    [Fact]
    public async Task A_row_with_no_identifier_for_the_source_is_never_absent()
    {
        // The protection is structural rather than configured: a
        // MyAnimeList-only title, or one added by hand, cannot be reached by an
        // AniList policy whatever the setting says.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await using (var setup = fixture.Database.CreateContext())
        {
            var malOnly = await SeedData.CreateAnimeAsync(
                setup, "Hoshi no Koe", AnimeSource.MyAnimeList, "555");
            var manual = await SeedData.CreateAnimeAsync(setup, "Something Handwritten");

            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, malOnly.Id));
            setup.LibraryEntries.Add(SeedData.Entry(Profile.DefaultProfileId, manual.Id));
            await setup.SaveChangesAsync();
        }

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(0, result.AbsentFlagged);

        await using var context = fixture.Database.CreateContext();

        Assert.False(await context.AnimeExternalIds.AnyAsync(x => x.MissingFromSourceAt != null));
        Assert.Equal(3, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task An_empty_list_flags_nothing()
    {
        // A truncated response, a paging bug, a mistyped account and "the user
        // deleted everything" are indistinguishable from here, so an empty fetch
        // concludes nothing at all.
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.Returns(ListResponse([]));

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(0, result.AbsentFlagged);

        await using var context = fixture.Database.CreateContext();
        Assert.False(await context.AnimeExternalIds.AnyAsync(x => x.MissingFromSourceAt != null));
        Assert.Equal(2, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Deleting_an_absent_title_takes_its_queue_slot_with_it()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await ConfigureAsync(fixture, s => s with { AbsencePolicy = SyncAbsencePolicy.Remove });

        var leavingId = await AnimeIdForAsync(fixture, "900102");

        await using (var setup = fixture.Database.CreateContext())
        {
            setup.QueueItems.Add(SeedData.QueueSlot(Profile.DefaultProfileId, 0, leavingId));
            await setup.SaveChangesAsync();
        }

        fixture.Client.Returns(OneTitle());

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(1, result.AbsentRemoved);
        Assert.Equal(0, result.AbsentFlagged);

        await using var context = fixture.Database.CreateContext();

        Assert.Equal(1, await context.LibraryEntries.CountAsync());

        // The slot goes in the same unit of work. AdvanceAsync reads a slot whose
        // library entry is missing as unknown rather than watched and keeps it, so an
        // entry deleted alone would leave a slot nothing could ever clear.
        Assert.False(await context.QueueItems.AnyAsync());

        // The catalogue row stays: relation edges and recommendation history point at
        // it, and it is also what stops the next fetch offering the deletion again.
        Assert.Equal(2, await context.Anime.CountAsync());
        Assert.False(await context.AnimeExternalIds.AnyAsync(x => x.MissingFromSourceAt != null));
    }

    [Fact]
    public async Task Too_many_absences_at_once_delete_nothing_and_wait_for_the_user()
    {
        // A list gone private, a short page and a rate limit all arrive looking like
        // this. The cap is what stops the one reading that cannot be undone.
        var everything = ListResponse(
            [.. Enumerable.Range(0, 7).Select(i => new AniListEntry(900200 + i, $"Title {i}"))]);

        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(everything));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await ConfigureAsync(fixture, s => s with { AbsencePolicy = SyncAbsencePolicy.Remove });

        // Six of the seven stop being listed, against a cap of five.
        fixture.Client.Returns(ListResponse([new AniListEntry(900200, "Title 0")]));

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(0, result.AbsentRemoved);
        Assert.Equal(6, result.AbsentFlagged);

        await using var context = fixture.Database.CreateContext();

        Assert.Equal(7, await context.LibraryEntries.CountAsync());

        // Held rather than dropped, so the user still hears about it.
        var status = await AniListStatusAsync(fixture);
        Assert.Equal(6, status.AbsentCount);
    }

    [Fact]
    public async Task An_absence_under_the_cap_still_deletes()
    {
        // The other side of the same boundary, so the cap cannot quietly become
        // "never delete anything".
        var everything = ListResponse(
            [.. Enumerable.Range(0, 7).Select(i => new AniListEntry(900200 + i, $"Title {i}"))]);

        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(everything));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await ConfigureAsync(fixture, s => s with { AbsencePolicy = SyncAbsencePolicy.Remove });

        // Five of the seven, which is exactly the cap rather than over it.
        fixture.Client.Returns(ListResponse(
            [new AniListEntry(900200, "Title 0"), new AniListEntry(900201, "Title 1")]));

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(5, result.AbsentRemoved);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(2, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Ignoring_absence_looks_for_nothing()
    {
        await using var fixture = await SyncFixture.CreateAsync(new StubAniListClient(TwoTitles()));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        await ConfigureAsync(fixture, s => s with { AbsencePolicy = SyncAbsencePolicy.Ignore });

        fixture.Client.Returns(OneTitle());

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(0, result.AbsentFlagged);

        await using var context = fixture.Database.CreateContext();
        Assert.False(await context.AnimeExternalIds.AnyAsync(x => x.MissingFromSourceAt != null));
    }

    [Fact]
    public async Task A_failed_fetch_records_a_failure_and_leaves_the_library_alone()
    {
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera"))
            {
                FailWith = "AniList has no such user, or the list is private."
            });

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.False(result.ChangedLibrary);

        var run = await fixture.LastRunAsync();
        Assert.Equal(SyncOutcome.Failed, run!.Outcome);
        Assert.Equal("AniList has no such user, or the list is private.", run.FailureReason);
    }

    [Fact]
    public async Task The_kill_switch_records_nothing()
    {
        // Nothing was attempted, and a log of runs that never ran would bury the
        // failures that did.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")),
            Configured(enabled: false));

        var result = await fixture.Service.RunUnattendedAsync(
            Profile.DefaultProfileId, AnimeSource.AniList);

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.Null(await fixture.LastRunAsync());
    }

    [Fact]
    public async Task Status_separates_the_last_success_from_the_last_failure()
    {
        // "Last synced 3 hours ago, last attempt failed: profile is private" is
        // actionable; either half on its own is not.
        await using var fixture = await SyncFixture.CreateAsync(
            new StubAniListClient(Response(900101, "Sora no Kakera")));

        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        fixture.Client.FailWith = "AniList returned 502.";

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);
        }

        var status = await AniListStatusAsync(fixture);

        Assert.Equal(SyncOutcome.Succeeded, status.LastSuccess!.Outcome);
        Assert.Equal(SyncOutcome.Failed, status.LastFailure!.Outcome);
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.True(status.IsStalled);

        // And it recovers: one run that reaches the source clears the count, so the
        // banner goes away without anybody dismissing it.
        fixture.Client.FailWith = null;
        await fixture.Service.RunUnattendedAsync(Profile.DefaultProfileId, AnimeSource.AniList);

        var recovered = await AniListStatusAsync(fixture);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.False(recovered.IsStalled);
        Assert.NotNull(recovered.LastFailure);
    }

    /// <summary>
    /// The AniList status specifically, because the page now accounts for every
    /// source rather than only the ones something can be fetched from.
    /// </summary>
    private static async Task<SourceSyncStatus> AniListStatusAsync(SyncFixture fixture) =>
        (await fixture.Service.GetStatusAsync(Profile.DefaultProfileId))
            .Single(s => s.Source == AnimeSource.AniList);

    /// <summary>Which catalogue row a sync gave one AniList identifier.</summary>
    private static async Task<int> AnimeIdForAsync(SyncFixture fixture, string externalId)
    {
        await using var context = fixture.Database.CreateContext();

        return await context.AnimeExternalIds
            .Where(x => x.Source == AnimeSource.AniList && x.ExternalId == externalId)
            .Select(x => x.AnimeId)
            .SingleAsync();
    }
}

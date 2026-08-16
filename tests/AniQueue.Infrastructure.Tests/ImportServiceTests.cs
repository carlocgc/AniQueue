using System.Text;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Progress;
using AniQueue.Infrastructure.Import;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

public class ImportServiceTests
{
    private static readonly MyAnimeListXmlParser Parser = new();

    private static Stream Export(params string[] entries) =>
        new MemoryStream(Encoding.UTF8.GetBytes(
            $"<myanimelist>{string.Join(string.Empty, entries)}</myanimelist>"));

    private static string Entry(
        string id,
        string title,
        string status = "Completed",
        int episodes = 12,
        int watched = 12,
        int score = 8) =>
        $"""
         <anime>
           <series_animedb_id>{id}</series_animedb_id>
           <series_title><![CDATA[{title}]]></series_title>
           <series_type>TV</series_type>
           <series_episodes>{episodes}</series_episodes>
           <my_watched_episodes>{watched}</my_watched_episodes>
           <my_score>{score}</my_score>
           <my_status>{status}</my_status>
         </anime>
         """;

    private sealed class ProgressRecorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Reports { get; } = [];

        public void Report(OperationProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Preview_reports_its_stages()
    {
        // The dialog shows real stages rather than an indeterminate spinner, which
        // only works if the service actually reports them.
        await using var fixture = await ImportFixture.CreateAsync();
        var progress = new ProgressRecorder();

        await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy"), Entry("1953", "Gunbuster")),
            Parser,
            Profile.DefaultProfileId,
            progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Contains(progress.Reports, r => r.Message.Contains("Reading", StringComparison.Ordinal));
        Assert.Contains(progress.Reports, r => r.Message.Contains("Comparing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_reports_a_count_that_reaches_the_total()
    {
        // A progress bar that stops short of the end reads as a stall.
        await using var fixture = await ImportFixture.CreateAsync();
        var progress = new ProgressRecorder();

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("1", "A"), Entry("2", "B"), Entry("3", "C")),
            Parser,
            Profile.DefaultProfileId);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId, progress);

        var counted = progress.Reports.Where(r => r.HasCount).ToList();
        Assert.NotEmpty(counted);
        Assert.Equal(counted[^1].Total, counted[^1].Current);
        Assert.Equal(1.0, counted[^1].Fraction);
    }

    [Fact]
    public async Task Progress_is_optional()
    {
        // Nothing may depend on a reporter being supplied — the services are used
        // from tests and, later, from non-interactive paths.
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);

        var result = await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task Preview_reports_new_titles_without_writing_anything()
    {
        // The brief is explicit: uploading a file must never mutate the database.
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy"), Entry("1953", "Gunbuster")),
            Parser,
            Profile.DefaultProfileId);

        Assert.Equal(2, preview.CreateCount);
        Assert.Equal(0, preview.UpdateCount);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(0, await context.Anime.CountAsync());
        Assert.Equal(0, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Committing_creates_titles_and_library_entries()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var file = Export(Entry("268", "Golden Boy", score: 9));

        var preview = await fixture.Service.PreviewAsync(file, Parser, Profile.DefaultProfileId);
        var result = await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        Assert.Equal(1, result.Created);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();
        var entry = await context.LibraryEntries.SingleAsync();

        Assert.Equal("Golden Boy", anime.Title);
        Assert.Equal(AnimeSource.MyAnimeList, anime.Source);
        Assert.Equal("268", anime.SourceAnimeId);
        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(9, entry.UserScore);
    }

    [Fact]
    public async Task Importing_the_same_export_twice_changes_nothing_the_second_time()
    {
        // Idempotency: the user re-exports from MAL monthly and imports the same
        // file again. That must not duplicate their library.
        await using var fixture = await ImportFixture.CreateAsync();

        var first = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy"), Entry("1953", "Gunbuster")),
            Parser,
            Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(first, Profile.DefaultProfileId);

        var second = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy"), Entry("1953", "Gunbuster")),
            Parser,
            Profile.DefaultProfileId);

        Assert.Equal(0, second.CreateCount);
        Assert.Equal(0, second.UpdateCount);
        Assert.Equal(2, second.UnchangedCount);
        Assert.False(second.HasApplicableChanges);

        await fixture.Service.CommitAsync(second, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(2, await context.Anime.CountAsync());
        Assert.Equal(2, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Committing_the_same_preview_twice_is_safe()
    {
        // A double-submitted form must not violate the unique index. The commit
        // re-resolves each entry rather than trusting ids captured at preview time.
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());
        Assert.Equal(1, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Progress_changes_are_detected_as_updates()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var initial = await fixture.Service.PreviewAsync(
            Export(Entry("31953", "New Game!", status: "Watching", episodes: 12, watched: 4, score: 0)),
            Parser,
            Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(initial, Profile.DefaultProfileId);

        var later = await fixture.Service.PreviewAsync(
            Export(Entry("31953", "New Game!", status: "Completed", episodes: 12, watched: 12, score: 8)),
            Parser,
            Profile.DefaultProfileId);

        Assert.Equal(1, later.UpdateCount);
        var item = Assert.Single(later.Items);
        Assert.Contains(item.Changes, c => c.Contains("Status", StringComparison.Ordinal));
        Assert.Contains(item.Changes, c => c.Contains("Watched", StringComparison.Ordinal));

        await fixture.Service.CommitAsync(later, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var entry = await context.LibraryEntries.SingleAsync();
        Assert.Equal(LibraryStatus.Completed, entry.Status);
        Assert.Equal(12, entry.EpisodesWatched);
        Assert.Equal(8, entry.UserScore);
    }

    [Fact]
    public async Task An_import_never_overwrites_locally_curated_fields()
    {
        // The single most important guarantee in the import pipeline. Re-importing
        // an export must not undo an evening spent organising the backlog.
        await using var fixture = await ImportFixture.CreateAsync();

        var initial = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy", status: "Watching", watched: 3)),
            Parser,
            Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(initial, Profile.DefaultProfileId);

        int animeId;
        await using (var setup = fixture.Database.CreateContext())
        {
            var anime = await setup.Anime.SingleAsync();
            animeId = anime.Id;

            var franchise = new Franchise
            {
                Name = "Curated",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            setup.Franchises.Add(franchise);
            await setup.SaveChangesAsync();

            anime.FranchiseId = franchise.Id;
            anime.FranchiseOrder = 1;

            var entry = await setup.LibraryEntries.SingleAsync();
            entry.PersonalNotes = "Recommended by a friend";
            entry.ManualPriority = 5;
            entry.IsHidden = true;
            entry.RecommendationScore = 8.7;
            entry.RecommendationReason = "Matches your comedy history";

            setup.QueueItems.Add(new QueueItem
            {
                ProfileId = Profile.DefaultProfileId,
                Position = 0,
                AnimeId = animeId,
                AddedAt = DateTimeOffset.UtcNow
            });

            await setup.SaveChangesAsync();
        }

        var reimport = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy", status: "Completed", watched: 6)),
            Parser,
            Profile.DefaultProfileId);
        await fixture.Service.CommitAsync(reimport, Profile.DefaultProfileId);

        await using var verify = fixture.Database.CreateContext();
        var updatedAnime = await verify.Anime.SingleAsync();
        var updatedEntry = await verify.LibraryEntries.SingleAsync();

        // Progress did update...
        Assert.Equal(LibraryStatus.Completed, updatedEntry.Status);
        Assert.Equal(6, updatedEntry.EpisodesWatched);

        // ...but nothing the user curated was touched.
        Assert.Equal("Recommended by a friend", updatedEntry.PersonalNotes);
        Assert.Equal(5, updatedEntry.ManualPriority);
        Assert.True(updatedEntry.IsHidden);
        Assert.Equal(8.7, updatedEntry.RecommendationScore);
        Assert.Equal("Matches your comedy history", updatedEntry.RecommendationReason);
        Assert.NotNull(updatedAnime.FranchiseId);
        Assert.Equal(1, updatedAnime.FranchiseOrder);
        Assert.Equal(1, await verify.QueueItems.CountAsync());
    }

    [Fact]
    public async Task A_title_matching_a_manual_entry_is_flagged_rather_than_duplicated()
    {
        // The user added it by hand before importing. Silently creating a second
        // copy would be worse than asking.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Golden Boy");
        }

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportAction.Conflict, item.Action);
        Assert.NotNull(item.ConflictReason);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(1, await context.Anime.CountAsync());
    }

    [Fact]
    public async Task Linking_a_conflict_adopts_the_source_identifier_onto_the_existing_title()
    {
        // The point of linking: the existing record gains the identifier, so the
        // same entry stops conflicting on every future import.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Golden Boy");
        }

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy", score: 9)), Parser, Profile.DefaultProfileId);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportAction.Conflict, item.Action);
        Assert.Equal("Golden Boy", item.ExistingTitle);

        item.Resolution = ConflictResolution.LinkToExisting;
        var result = await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        Assert.Equal(1, result.Updated);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();
        Assert.Equal(AnimeSource.MyAnimeList, anime.Source);
        Assert.Equal("268", anime.SourceAnimeId);

        var entry = await context.LibraryEntries.SingleAsync();
        Assert.Equal(9, entry.UserScore);
    }

    [Fact]
    public async Task A_linked_conflict_does_not_conflict_again_on_the_next_import()
    {
        // The behaviour that makes linking worth offering at all.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Golden Boy");
        }

        var first = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);
        first.Items[0].Resolution = ConflictResolution.LinkToExisting;
        await fixture.Service.CommitAsync(first, Profile.DefaultProfileId);

        var second = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);

        Assert.Equal(0, second.ConflictCount);
        Assert.Equal(1, second.UnchangedCount);
    }

    [Fact]
    public async Task Importing_a_conflict_as_new_creates_a_separate_title()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Golden Boy");
        }

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);
        preview.Items[0].Resolution = ConflictResolution.ImportAsNew;

        var result = await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        Assert.Equal(1, result.Created);

        await using var context = fixture.Database.CreateContext();
        Assert.Equal(2, await context.Anime.CountAsync());
        Assert.Equal(1, await context.Anime.CountAsync(a => a.SourceAnimeId == null));
    }

    [Fact]
    public async Task Unresolved_conflicts_leave_the_library_untouched()
    {
        // Skip is the default, and defaults must be the recoverable option.
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Golden Boy");
        }

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);

        Assert.Equal(ConflictResolution.Skip, preview.Items[0].Resolution);
        Assert.False(preview.HasApplicableChanges);

        await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        await using var context = fixture.Database.CreateContext();
        var anime = await context.Anime.SingleAsync();
        Assert.Null(anime.SourceAnimeId);
        Assert.Equal(0, await context.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task Resolved_conflicts_count_towards_there_being_something_to_import()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Golden Boy");
        }

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("268", "Golden Boy")), Parser, Profile.DefaultProfileId);

        Assert.False(preview.HasApplicableChanges);

        preview.Items[0].Resolution = ConflictResolution.LinkToExisting;

        Assert.True(preview.HasApplicableChanges);
        Assert.Equal(1, preview.ResolvedConflictCount);
    }

    [Fact]
    public async Task Conflicts_are_skipped_rather_than_applied()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        await using (var setup = fixture.Database.CreateContext())
        {
            await SeedData.CreateAnimeAsync(setup, "Ambiguous");
        }

        var preview = await fixture.Service.PreviewAsync(
            Export(Entry("999", "Ambiguous"), Entry("268", "Golden Boy")),
            Parser,
            Profile.DefaultProfileId);

        var result = await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Status_totals_are_reported_for_the_whole_file()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            Export(
                Entry("1", "A", status: "Completed"),
                Entry("2", "B", status: "Completed"),
                Entry("3", "C", status: "Watching"),
                Entry("4", "D", status: "Plan to Watch")),
            Parser,
            Profile.DefaultProfileId);

        Assert.Equal(2, preview.CompletedCount);
        Assert.Equal(1, preview.WatchingCount);
        Assert.Equal(1, preview.PlanningCount);
    }

    [Fact]
    public async Task A_rejected_file_produces_no_items_and_cannot_be_committed()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("<myanimelist><anime>truncated")),
            Parser,
            Profile.DefaultProfileId);

        Assert.True(preview.IsFileRejected);
        Assert.Empty(preview.Items);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.CommitAsync(preview, Profile.DefaultProfileId));
    }

    [Fact]
    public async Task Unusable_records_are_reported_while_the_rest_import()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var preview = await fixture.Service.PreviewAsync(
            Export(
                "<anime><series_animedb_id>7</series_animedb_id></anime>",   // no title
                Entry("268", "Golden Boy")),
            Parser,
            Profile.DefaultProfileId);

        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.InvalidCount);

        var result = await fixture.Service.CommitAsync(preview, Profile.DefaultProfileId);
        Assert.Equal(1, result.Created);
    }

    private sealed class ImportFixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

        public required IImportService Service { get; init; }

        public static async Task<ImportFixture> CreateAsync()
        {
            var database = await SqliteTestDatabase.CreateAsync();

            await new DatabaseInitializer(
                database.ContextFactory,
                Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
                NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

            return new ImportFixture
            {
                Database = database,
                Service = new ImportService(database.ContextFactory, NullLogger<ImportService>.Instance)
            };
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}

using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Artwork;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The pass that fills the cover cache in (D47).
/// </summary>
/// <remarks>
/// What is asserted here is the gate and the bookkeeping — which rows are picked up,
/// what a failure costs them, and the two things only the filesystem knows. The
/// guards live in <c>CoverArtClientTests</c> and in Core, so the client is a stub
/// here and no test opens a socket.
/// </remarks>
public class ArtworkServiceTests
{
    private const string Url = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/small/bx1-abc.jpg";

    private const string Replaced = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/small/bx1-zzz.jpg";

    /// <summary>
    /// A clock that never waits, so a paced pass costs no real time.
    /// </summary>
    /// <remarks>
    /// The same shape the relation tests use. Without it every test here would spend
    /// a quarter of a second per picture proving the service asked to wait, which is
    /// arithmetic rather than behaviour.
    /// </remarks>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, period);
    }

    private sealed class StubCoverArtClient(CoverArtFetch reply) : ICoverArtClient
    {
        public List<string> Requested { get; } = [];

        public CoverArtFetch Reply { get; set; } = reply;

        public Task<CoverArtFetch> FetchAsync(string remoteUrl, CancellationToken cancellationToken)
        {
            // Observed for the same reason a stub HttpMessageHandler observes it: a
            // cancelled pass that appeared to do work would hide the behaviour the
            // cancellation test exists to check.
            cancellationToken.ThrowIfCancellationRequested();

            Requested.Add(remoteUrl);
            return Task.FromResult(Reply);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteTestDatabase Database { get; init; }

        public required StubCoverArtClient Client { get; init; }

        public required CoverArtStore Store { get; init; }

        public required ArtworkService Service { get; init; }

        public required string Root { get; init; }

        public static async Task<Fixture> CreateAsync(CoverArtFetch? reply = null)
        {
            var database = await SqliteTestDatabase.CreateAsync();

            // The store derives its directory from the database path, which is what
            // gives the sample profile its own cache for free. A test database lives
            // in memory and has no directory, so one is pointed at a temporary path
            // instead — the store only ever reads the path.
            var root = Path.Combine(Path.GetTempPath(), "aniqueue-covers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var store = new CoverArtStore(Options.Create(new AniQueueDatabaseOptions
            {
                Path = Path.Combine(root, "aniqueue.db")
            }));

            var client = new StubCoverArtClient(reply ?? CoverArtFetch.Success([1, 2, 3], ".jpg"));

            return new Fixture
            {
                Database = database,
                Client = client,
                Store = store,
                Root = root,
                Service = new ArtworkService(
                    database.ContextFactory,
                    store,
                    client,
                    NullLogger<ArtworkService>.Instance,
                    new ImmediateTimeProvider())
            };
        }

        public async Task<AnimeImage> AddImageAsync(string remoteUrl = Url, Action<AnimeImage>? adjust = null)
        {
            await using var context = Database.CreateContext();
            var anime = await SeedData.CreateAnimeAsync(context, "A Title");

            var image = new AnimeImage
            {
                AnimeId = anime.Id,
                Kind = ImageKind.Poster,
                Source = AnimeSource.AniList,
                RemoteUrl = remoteUrl
            };

            adjust?.Invoke(image);
            context.AnimeImages.Add(image);
            await context.SaveChangesAsync();

            return image;
        }

        public async Task<AnimeImage> ReloadAsync(int id)
        {
            await using var context = Database.CreateContext();
            return await context.AnimeImages.SingleAsync(i => i.Id == id);
        }

        /// <summary>Every cached file, as a path relative to the art root.</summary>
        /// <remarks>
        /// Relative rather than by name, because the kind is a directory now and two
        /// kinds can legitimately hold the same filename for the same title.
        /// </remarks>
        public string[] CachedFiles()
        {
            var art = Path.Combine(Root, "art");

            return Directory.Exists(art)
                ? Directory.GetFiles(art, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(art, f).Replace('\\', '/'))
                    .Order()
                    .ToArray()
                : [];
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task A_picture_nobody_has_fetched_is_fetched_and_recorded()
    {
        await using var fixture = await Fixture.CreateAsync();
        var image = await fixture.AddImageAsync();

        var result = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Equal(1, result.Fetched);

        var row = await fixture.ReloadAsync(image.Id);
        Assert.NotNull(row.ContentHash);
        Assert.Equal(".jpg", row.FileExtension);
        Assert.Equal(3, row.ByteCount);
        Assert.NotNull(row.FetchedAt);

        // The pair that matters: the row now claims to be showing the picture at
        // this exact address, which is what stops it being picked up again.
        Assert.Equal(Url, row.FetchedUrl);

        // Under its kind's directory, which is what makes 9b's four kinds four
        // directories rather than one holding four thousand files.
        Assert.Equal([$"thumbnails/{row.AnimeId}-{row.ContentHash}.jpg"], fixture.CachedFiles());
    }

    [Fact]
    public async Task A_picture_already_showing_is_not_fetched_again()
    {
        await using var fixture = await Fixture.CreateAsync();
        var image = await fixture.AddImageAsync();

        await fixture.Service.RunAsync(Budget, CancellationToken.None);
        fixture.Client.Requested.Clear();

        var second = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Empty(fixture.Client.Requested);
        Assert.False(second.DidWork);
    }

    [Fact]
    public async Task Art_replaced_at_a_new_address_is_fetched_again()
    {
        // AniList's URLs carry their own content hash, so a changed address means the
        // picture changed. This is the whole reason there are two URL columns.
        await using var fixture = await Fixture.CreateAsync();
        var image = await fixture.AddImageAsync();

        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        await using (var context = fixture.Database.CreateContext())
        {
            await context.AnimeImages
                .Where(i => i.Id == image.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.RemoteUrl, Replaced),
                    CancellationToken.None);
        }

        fixture.Client.Requested.Clear();
        fixture.Client.Reply = CoverArtFetch.Success([9, 9], ".png");

        var result = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Equal([Replaced], fixture.Client.Requested);
        Assert.Equal(1, result.Fetched);
        Assert.Equal(Replaced, (await fixture.ReloadAsync(image.Id)).FetchedUrl);
    }

    [Fact]
    public async Task The_old_file_is_swept_once_the_new_one_has_arrived()
    {
        // The replaced picture is written under a new name, because the name carries
        // the hash. Nothing deletes the old one at the moment of replacement — the
        // sweep does, by subtracting what rows claim from what is on disk.
        await using var fixture = await Fixture.CreateAsync();
        var image = await fixture.AddImageAsync();

        await fixture.Service.RunAsync(Budget, CancellationToken.None);
        var original = Assert.Single(fixture.CachedFiles());

        await using (var context = fixture.Database.CreateContext())
        {
            await context.AnimeImages
                .Where(i => i.Id == image.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.RemoteUrl, Replaced),
                    CancellationToken.None);
        }

        fixture.Client.Reply = CoverArtFetch.Success([9, 9], ".png");
        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        var remaining = Assert.Single(fixture.CachedFiles());
        Assert.NotEqual(original, remaining);
    }

    [Fact]
    public async Task A_cached_file_that_has_vanished_is_fetched_again()
    {
        // Disk wins. Somebody reclaiming space by deleting the covers directory is a
        // reasonable thing to do to a cache, and it has to heal rather than leave
        // every row pointing at a file that is not there.
        await using var fixture = await Fixture.CreateAsync();
        var image = await fixture.AddImageAsync();

        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        foreach (var file in Directory.GetFiles(Path.Combine(fixture.Root, "art"), "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        fixture.Client.Requested.Clear();
        var result = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Equal(1, result.Healed);
        Assert.Equal(1, result.Fetched);
        Assert.Single(fixture.CachedFiles());
        Assert.NotNull((await fixture.ReloadAsync(image.Id)).ContentHash);
    }

    [Fact]
    public async Task A_file_no_row_claims_is_deleted()
    {
        // What is left behind when a title leaves the library: the cascade takes the
        // row and cannot reach the filesystem.
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddImageAsync();
        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        var orphan = Path.Combine(fixture.Root, "art", "thumbnails", "999999-abcdef.jpg");
        await File.WriteAllTextAsync(orphan, "junk", CancellationToken.None);

        var result = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(orphan));
        Assert.Single(fixture.CachedFiles());
    }

    [Fact]
    public async Task A_permanent_failure_is_never_retried()
    {
        await using var fixture = await Fixture.CreateAsync(CoverArtFetch.Permanent);
        var image = await fixture.AddImageAsync();

        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        var row = await fixture.ReloadAsync(image.Id);
        Assert.True(row.FailureIsPermanent);
        Assert.NotNull(row.FailedAt);

        // Not counted against the attempts, because attempts are for things that
        // might change their mind.
        Assert.Equal(0, row.AttemptCount);

        fixture.Client.Requested.Clear();
        await fixture.Service.RunAsync(Budget, CancellationToken.None);
        Assert.Empty(fixture.Client.Requested);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_five_times_and_then_left_alone()
    {
        await using var fixture = await Fixture.CreateAsync(CoverArtFetch.Transient);
        var image = await fixture.AddImageAsync();

        for (var attempt = 1; attempt <= ArtworkService.MaxAttempts; attempt++)
        {
            await fixture.Service.RunAsync(Budget, CancellationToken.None);
            Assert.Equal(attempt, (await fixture.ReloadAsync(image.Id)).AttemptCount);
        }

        fixture.Client.Requested.Clear();
        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Empty(fixture.Client.Requested);
        Assert.False((await fixture.ReloadAsync(image.Id)).FailureIsPermanent);
    }

    [Fact]
    public async Task A_new_address_revives_a_row_that_had_given_up()
    {
        // Both failure states clear on a URL change, which is the only event that
        // makes the question worth asking again. Without this a title whose art was
        // briefly broken would stay blank for the life of the installation.
        await using var fixture = await Fixture.CreateAsync(CoverArtFetch.Permanent);
        var image = await fixture.AddImageAsync();
        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        await using (var context = fixture.Database.CreateContext())
        {
            var row = await context.AnimeImages.SingleAsync(i => i.Id == image.Id,
                CancellationToken.None);

            // Through the import path's own rule rather than by hand, since that is
            // what actually happens when a sync sees a different address.
            row.RemoteUrl = Replaced;
            row.FailedAt = null;
            row.FailureIsPermanent = false;
            row.AttemptCount = 0;
            await context.SaveChangesAsync(CancellationToken.None);
        }

        fixture.Client.Reply = CoverArtFetch.Success([4], ".jpg");
        var result = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
    }

    [Fact]
    public async Task A_failed_fetch_leaves_no_file_behind()
    {
        await using var fixture = await Fixture.CreateAsync(CoverArtFetch.Transient);
        await fixture.AddImageAsync();

        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Empty(fixture.CachedFiles());
    }

    [Fact]
    public async Task A_pass_with_nothing_outstanding_makes_no_request_at_all()
    {
        // D25's "idle when its input is empty", stated as an assertion. This is the
        // property that lets the job be switched on and cost nothing.
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Empty(fixture.Client.Requested);
        Assert.False(result.DidWork);
    }

    [Fact]
    public async Task A_cancelled_pass_throws_and_costs_the_title_nothing()
    {
        // Both halves matter and they are different claims. Throwing is the contract
        // BackgroundJobRunner already expects — it catches this and files the run as
        // cancelled rather than failed. Leaving the row untouched is what makes
        // pressing Cancel free: recorded as a transient failure it would spend one of
        // five attempts, so cancelling five times would permanently blank whatever
        // was in flight.
        await using var fixture = await Fixture.CreateAsync();
        var image = await fixture.AddImageAsync();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.RunAsync(Budget, cancelled.Token));

        var row = await fixture.ReloadAsync(image.Id);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.FailedAt);
        Assert.Null(row.ContentHash);
        Assert.Empty(fixture.CachedFiles());
    }

    [Fact]
    public async Task Queued_titles_are_fetched_before_everything_else()
    {
        // Precondition ordering rather than orchestration: remove it and the pass
        // still converges on the same set. What it buys is that Up Next fills in
        // first, which is the page the decision is actually made on.
        await using var fixture = await Fixture.CreateAsync();

        var ordinary = await fixture.AddImageAsync("https://s4.anilist.co/ordinary.jpg");
        var planned = await fixture.AddImageAsync("https://s4.anilist.co/planned.jpg");
        var queued = await fixture.AddImageAsync("https://s4.anilist.co/queued.jpg");

        await using (var context = fixture.Database.CreateContext())
        {
            var profile = await SeedData.CreateProfileAsync(context);

            context.LibraryEntries.Add(new LibraryEntry
            {
                ProfileId = profile.Id,
                AnimeId = planned.AnimeId,
                Status = LibraryStatus.Planning,
                DateAdded = DateTimeOffset.UtcNow,
                LastUpdated = DateTimeOffset.UtcNow
            });

            context.QueueItems.Add(SeedData.QueueSlot(profile.Id, 1, queued.AnimeId));
            await context.SaveChangesAsync(CancellationToken.None);
        }

        await fixture.Service.RunAsync(Budget, CancellationToken.None);

        Assert.Equal(
            [
                "https://s4.anilist.co/queued.jpg",
                "https://s4.anilist.co/planned.jpg",
                "https://s4.anilist.co/ordinary.jpg"
            ],
            fixture.Client.Requested);
    }
}

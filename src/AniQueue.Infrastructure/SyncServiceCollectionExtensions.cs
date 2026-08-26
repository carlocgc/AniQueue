using System.Net;
using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Jobs;
using AniQueue.Core.Library;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure;

public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AniList parser and the client that feeds it.
    /// </summary>
    /// <remarks>
    /// Separate from <c>AddAniQueuePersistence</c> because these are the only
    /// services in the application that talk to anything outside it, and a
    /// deployment that has not configured an account should be able to see that in
    /// one place.
    /// </remarks>
    public static IServiceCollection AddAniQueueSync(this IServiceCollection services)
    {
        // Keyed like every other parser, and only keyed. It briefly needed a second,
        // concrete registration so a sync could pass the title-language preference to
        // an overload the interface could not express; storing each title against its
        // language removed the reason for both (D22).
        services.AddKeyedSingleton<IAnimeListParser, AniListJsonParser>(AnimeSource.AniList);

        services.AddSingleton<IAniListClient>(serviceProvider => new AniListClient(
            CreateHttpClient(),
            serviceProvider.GetRequiredService<ILogger<AniListClient>>()));

        // Scoped like the import service it delegates to: it opens short-lived
        // contexts through the factory (D3) and holds nothing between calls.
        services.AddScoped<ISyncService, Sync.SyncService>();

        // The settings file this section's options are read from is no longer written
        // here: D36 made one component read and write it for every feature that has a
        // setting, and AddAniQueueSettings registers it.

        // What every task's runs are written to and its cadence measured from (D40).
        // Scoped like everything else that opens a context, and registered here
        // because the jobs beside it are its only writers.
        services.AddScoped<IJobRunStore, Jobs.JobRunStore>();

        // Scoped, and resolved once per tick by whatever runs it. The job holds no
        // state between runs deliberately: what it needs to know about the last one
        // is in the run record, which survives a restart where a field would not.
        services.AddScoped<Sync.UnattendedSyncJob>();

        // The second job, and the first of D25's enrichment passes. It needs no run
        // record and no schedule setting: what it has left to do is a count of rows
        // with no marker, which is a question the database answers.
        services.AddScoped<IRelationBackfill, Sync.RelationBackfillService>();
        services.AddScoped<Sync.RelationBackfillJob>();

        // The third, and the first that fetches something other than JSON (D47). The
        // store is a singleton because it is a path and nothing else; the service is
        // scoped like every other job body.
        services.AddSingleton<Artwork.CoverArtStore>();

        // A singleton holding one long-lived client, exactly as AniListClient is —
        // and not registered as a bare HttpClient, which would hand the image client
        // to anything that ever asks for one.
        services.AddSingleton<ICoverArtClient>(serviceProvider => new Artwork.CoverArtClient(
            CreateImageHttpClient(),
            serviceProvider.GetRequiredService<ILogger<Artwork.CoverArtClient>>()));

        services.AddScoped<IArtworkService, Artwork.ArtworkService>();
        services.AddScoped<Artwork.CoverArtJob>();

        // A singleton because it is a rendezvous between things with no other way to
        // reach each other: a background scope publishing, and every open circuit
        // listening. Registered here rather than with the pages that subscribe,
        // because the publisher is the half that could not otherwise be wired.
        services.AddSingleton<ILibraryChangeNotifier, Library.LibraryChangeNotifier>();

        // A singleton for the same reason, and one object behind two interfaces: the
        // page reads rows and asks for runs, the runner waits for those requests and
        // says what it is doing. Neither should be able to do the other's half, and
        // both are looking at one piece of state (D40).
        services.AddSingleton<Jobs.TaskRegistry>();
        services.AddSingleton<ITaskRegistry>(s => s.GetRequiredService<Jobs.TaskRegistry>());
        services.AddSingleton<ITaskRunnerBridge>(s => s.GetRequiredService<Jobs.TaskRegistry>());

        return services;
    }

    /// <summary>
    /// Builds the one <see cref="HttpClient"/> AniQueue owns.
    /// </summary>
    /// <remarks>
    /// Constructed here rather than through <c>AddHttpClient</c>, which would mean
    /// taking <c>Microsoft.Extensions.Http</c> as a dependency of Infrastructure to
    /// manage a single long-lived client to a single host — and §12 requires
    /// approval for a new package. The two things the factory would have given us
    /// are done explicitly instead: a pooled connection lifetime so a long-running
    /// container still notices DNS changes, and one shared instance rather than a
    /// socket-exhausting one per call.
    /// </remarks>
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // The endpoint sets a laravel_session cookie. Nothing here has any use
            // for it, and carrying a session across requests to a public endpoint is
            // state this application should not be holding.
            UseCookies = false,

            AutomaticDecompression = DecompressionMethods.All,

            // Without this a singleton client caches DNS for the life of the
            // process, which for a self-hosted container can be months.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            // Long enough for a large list, short enough that a hung endpoint does
            // not hold a user-initiated sync open indefinitely.
            Timeout = TimeSpan.FromSeconds(30),

            // 424 KB carried 753 entries at full fidelity, so a few thousand titles
            // is a few megabytes. Sized with headroom rather than tightly (§6), and
            // enforced by HttpClient itself so an oversized body is refused while it
            // is still arriving.
            MaxResponseContentBufferSize = 16 * 1024 * 1024
        };

        client.DefaultRequestHeaders.Accept.Add(new("application/json"));

        // Identifying the client is ordinary manners toward a free public API, and
        // it is what makes an operator's traffic recognisable if it ever needs to be
        // discussed with them. No version of anything personal goes in it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AniQueue (self-hosted)");

        return client;
    }

    /// <summary>
    /// The client that fetches pictures, which is deliberately not the one above.
    /// </summary>
    /// <remarks>
    /// Separate because almost every setting differs, and two of them are load
    /// bearing rather than tuning. <b>Redirects are not followed</b>: the host
    /// allowlist vouches for the address that was asked for and can vouch for nothing
    /// about where that address points, so a 3xx is a refusal rather than a hop
    /// (D47, §6). And the buffer limit is the size cap enforced by the transport, so
    /// an oversized body is refused while it is still arriving rather than after it
    /// has been read into memory.
    ///
    /// Built by hand for the same reason the other one is: <c>AddHttpClient</c> would
    /// mean taking <c>Microsoft.Extensions.Http</c> as a dependency, and §12 requires
    /// approval for a package to manage two long-lived clients.
    /// </remarks>
    private static HttpClient CreateImageHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,

            // Already-compressed bytes. Asking for an encoding would spend CPU on
            // both ends to make a JPEG very slightly larger.
            AutomaticDecompression = DecompressionMethods.None,

            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            // A cover is ten kilobytes. Anything still arriving after fifteen seconds
            // is a moment worth abandoning and trying again later, which is exactly
            // what a transient failure means here.
            Timeout = TimeSpan.FromSeconds(15),

            MaxResponseContentBufferSize = ImageSource.MaxByteCount
        };

        client.DefaultRequestHeaders.Accept.Add(new("image/*"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AniQueue (self-hosted)");

        return client;
    }
}

using System.Net;
using AniQueue.Core.Domain;
using AniQueue.Core.Import;
using AniQueue.Core.Sync;
using AniQueue.Infrastructure.Sync;
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
        // Registered twice, deliberately, and sharing one instance. The keyed
        // registration is how every parser is resolved (a bare IAnimeListParser
        // would silently rebind whichever was registered last); the concrete one
        // exists because a sync passes the user's title-language preference, and
        // that argument has no place on an interface a MyAnimeList export also
        // implements (D22).
        services.AddSingleton<AniListJsonParser>();
        services.AddKeyedSingleton<IAnimeListParser>(
            AnimeSource.AniList,
            (serviceProvider, _) => serviceProvider.GetRequiredService<AniListJsonParser>());

        services.AddSingleton<IAniListClient>(serviceProvider => new AniListClient(
            CreateHttpClient(),
            serviceProvider.GetRequiredService<ILogger<AniListClient>>()));

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
}

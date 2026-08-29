using System.Net;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AniQueue.Infrastructure;

public static class SettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the reader and writer of <c>userconfig.json</c>.
    /// </summary>
    /// <remarks>
    /// Its own extension rather than a line inside <c>AddAniQueueSync</c>. Settings are
    /// not sync's business — scoring writes them too, and the settings page reads all
    /// of them — and registering a shared component under one feature is how it comes
    /// to look like that feature's helper.
    ///
    /// <b>Singleton</b>, because it holds nothing per request and its two collaborators
    /// are singletons: the configuration root it reloads, and the status the banner
    /// reads.
    /// </remarks>
    public static IServiceCollection AddAniQueueSettings(this IServiceCollection services)
    {
        services.AddSingleton<IUserSettingsStore, UserSettingsStore>();

        return services;
    }

    /// <summary>
    /// Registers the courier that carries a scoring request to a hosted model.
    /// </summary>
    /// <remarks>
    /// <b>Its own <see cref="HttpClient"/>, not the one AniList uses.</b> That client's
    /// thirty-second timeout is right for fetching a list and absurd for a model that
    /// may think for ten minutes, and a single ceiling covering both would have to be
    /// the larger — which would leave a hung AniList fetch holding a user-initiated
    /// sync open for the rest of the afternoon.
    ///
    /// The per-attempt timeout is a linked token inside the endpoint rather than
    /// <see cref="HttpClient.Timeout"/>, so changing the setting takes effect without
    /// rebuilding the client. What stays here is what cannot change at runtime.
    /// </remarks>
    public static IServiceCollection AddAniQueueScoringEndpoint(this IServiceCollection services)
    {
        // Scoped, and resolved once per tick by whatever runs it, like every other
        // background job here. It holds nothing between runs: what it needs to know
        // about the last one is in the run record, which survives a restart.
        services.AddScoped<ScoringSweepJob>();

        services.AddSingleton<IScoringEndpoint>(serviceProvider =>
        {
            var handler = new SocketsHttpHandler
            {
                // Nothing here has any use for a session, and a chat-completions server
                // has no business setting one on this application's behalf.
                UseCookies = false,

                AutomaticDecompression = DecompressionMethods.All,

                // A long-lived container would otherwise cache DNS for months, which
                // matters more here than for AniList: a model server on the operator's
                // own network is exactly the kind of host whose address changes.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                // Deliberately past any sane setting rather than equal to it. The real
                // bound is the linked token the endpoint applies per attempt; this is
                // only a backstop, and one that fired first would produce a timeout
                // message quoting a number the user never chose.
                Timeout = TimeSpan.FromHours(2),

                // A ranking of a large backlog is tens of kilobytes and the parser
                // refuses anything past its own limit anyway, so this is sized to
                // refuse a hostile or malfunctioning endpoint while the body is still
                // arriving rather than to bound a legitimate reply.
                MaxResponseContentBufferSize = 32 * 1024 * 1024
            };

            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AniQueue (self-hosted)");

            return new ChatCompletionsEndpoint(
                client,
                serviceProvider.GetRequiredService<IOptionsMonitor<ScoringOptions>>(),
                serviceProvider.GetRequiredService<ILogger<ChatCompletionsEndpoint>>());
        });

        return services;
    }
}

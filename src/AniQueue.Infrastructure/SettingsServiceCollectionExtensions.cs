using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AniQueue.Infrastructure;

public static class SettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the reader and writer of <c>userconfig.json</c> (D36).
    /// </summary>
    /// <remarks>
    /// Its own extension rather than a line inside <c>AddAniQueueSync</c>, where the
    /// first-boot template used to live. Settings are not sync's business — scoring
    /// writes them too, and Phase 10's page will read all of them — and leaving the
    /// registration under the first feature that happened to need it is how a shared
    /// component comes to look like one feature's helper.
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
}

using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AniQueue.Infrastructure;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database context factory and startup initialiser.
    /// </summary>
    /// <remarks>
    /// Configuration is passed in as a delegate rather than an <c>IConfiguration</c>
    /// so that Infrastructure does not take a dependency on the configuration
    /// binding packages purely to read one section. The caller binds; this
    /// registers.
    /// </remarks>
    public static IServiceCollection AddAniQueuePersistence(
        this IServiceCollection services,
        Action<AniQueueDatabaseOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);

        // A factory, not a scoped context (D3). Under Blazor Interactive Server a
        // scoped service lives for the whole SignalR circuit, so a scoped DbContext
        // would accumulate tracked entities for hours and fail as soon as two
        // components rendered concurrently.
        services.AddDbContextFactory<AniQueueDbContext>((serviceProvider, builder) =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AniQueueDatabaseOptions>>()
                .Value;

            builder.UseSqlite(
                options.BuildConnectionString(),
                sqlite => sqlite.MigrationsAssembly(typeof(AniQueueDbContext).Assembly.GetName().Name));
        });

        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<Core.Import.IImportService, Import.ImportService>();
        services.AddScoped<Core.Library.ILibraryService, Library.LibraryService>();

        // Registered beside the library rather than with the sync services, because
        // it reads the graph rather than fills it: the backfill is outbound traffic
        // gated on a kill switch, this is a query the backlog makes (D24).
        services.AddScoped<Core.Library.IRelationService, Library.RelationService>();

        services.AddScoped<Core.Queue.IQueueService, Queue.QueueService>();

        // Parsers are pure and stateless, so a singleton is sufficient (D9), and
        // they are registered under a key rather than as a bare IAnimeListParser.
        // An unkeyed second registration would silently rebind the first, and the
        // symptom would be the import page quietly feeding XML to whichever parser
        // happened to be registered last.
        services.AddKeyedSingleton<Core.Import.IAnimeListParser, Core.Import.MyAnimeListXmlParser>(
            Core.Domain.AnimeSource.MyAnimeList);

        return services;
    }

    // There was a development seeder here, and it is gone rather than disabled
    // (D27). Sample titles carrying invented AniList identifiers are indistinguishable
    // from library rows the source has stopped listing, so the first real sync
    // reported five of them as missing — a warning about data the application had
    // invented for itself. An empty database is the honest starting state.
}

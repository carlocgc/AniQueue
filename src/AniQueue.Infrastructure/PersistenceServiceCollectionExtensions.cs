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

        // Parsers are pure and stateless, so a singleton is sufficient (D9).
        services.AddSingleton<Core.Import.IAnimeListParser, Core.Import.MyAnimeListXmlParser>();

        return services;
    }

    /// <summary>
    /// Registers the sample-data seeder. Deliberately separate from
    /// <see cref="AddAniQueuePersistence"/> so that a production container never
    /// even resolves the type, let alone runs it.
    /// </summary>
    public static IServiceCollection AddAniQueueDevelopmentSeeder(this IServiceCollection services)
    {
        services.AddScoped<Persistence.Seeding.DevelopmentSeeder>();
        return services;
    }
}

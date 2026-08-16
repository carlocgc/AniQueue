using AniQueue.Infrastructure;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Persistence.Seeding;
using AniQueue.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAniQueuePersistence(options =>
    builder.Configuration.GetSection(AniQueueDatabaseOptions.SectionName).Bind(options));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAniQueueDevelopmentSeeder();
}

var app = builder.Build();

// A self-hosted application that disappears without explanation is very hard to
// support: the operator sees a stopped container and an empty log. These handlers
// make the difference between the three ways it can end visible in the log.
//
// A graceful stop writes "shutting down". A fatal exception writes the exception.
// Silence means the process was killed from outside — by an orchestrator, an OOM
// killer, or an IDE ending its debug session — which is itself the diagnosis.
{
    var lifetimeLogger = app.Services.GetRequiredService<ILogger<Program>>();

    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        lifetimeLogger.LogCritical(
            e.ExceptionObject as Exception,
            "Unhandled exception; the process is terminating (runtime terminating: {IsTerminating})",
            e.IsTerminating);

    // Faulted tasks nobody awaited. Not fatal by default, but they indicate work
    // that failed silently, which is worth knowing about.
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        lifetimeLogger.LogError(e.Exception, "A background task failed and nothing observed the result");
        e.SetObserved();
    };

    app.Lifetime.ApplicationStopping.Register(() =>
        lifetimeLogger.LogInformation("AniQueue is shutting down"));

    app.Lifetime.ApplicationStopped.Register(() =>
        lifetimeLogger.LogInformation("AniQueue has stopped"));
}

// Bring the schema up to date before serving traffic. A database that cannot be
// reached is fatal: starting anyway would turn one clear startup error into an
// endless stream of confusing request failures.
try
{
    using var scope = app.Services.CreateScope();

    await scope.ServiceProvider
        .GetRequiredService<DatabaseInitializer>()
        .InitialiseAsync(app.Lifetime.ApplicationStopping);

    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider
            .GetRequiredService<DevelopmentSeeder>()
            .SeedAsync(app.Lifetime.ApplicationStopping);
    }
}
catch (Exception ex)
{
    app.Services.GetRequiredService<ILogger<Program>>()
        .LogCritical(ex, "Database initialisation failed; AniQueue cannot start");
    return 1;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

/// <summary>Exposed so integration tests and the logger category can name the entry point.</summary>
public partial class Program;

using AniQueue.Infrastructure;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Persistence.Seeding;
using AniQueue.Infrastructure.Sync;
using AniQueue.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Operator settings the self-hoster edits from outside the application, kept
// beside the database in their volume rather than inside the image (D20). The
// database path is read before this file is added because it is what says where
// "beside the database" is; everything else, including the path itself for the
// binding below, can still be overridden by it.
//
// reloadOnChange is set, but nothing may depend on it: the file watcher behind it
// does not reliably fire on Windows-host or network-share bind mounts, so a
// restart has to apply the file too.
var databasePath = builder.Configuration[$"{AniQueueDatabaseOptions.SectionName}:Path"]
    ?? new AniQueueDatabaseOptions().Path;

var dataDirectory = Path.GetDirectoryName(databasePath);

if (!string.IsNullOrEmpty(dataDirectory))
{
    builder.Configuration.AddJsonFile(
        Path.Combine(dataDirectory, UserConfigTemplate.FileName), optional: true, reloadOnChange: true);
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAniQueuePersistence(options =>
    builder.Configuration.GetSection(AniQueueDatabaseOptions.SectionName).Bind(options));

// Bound to the live section rather than through a delegate, unlike the database
// options above: this is the half of configuration an operator edits while the
// application is running, and a section binding is what lets a reload reach the
// options monitor at all.
builder.Services.Configure<SyncOptions>(
    builder.Configuration.GetSection(SyncOptions.SectionName));

builder.Services.AddAniQueueSync();

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

// Leave the operator a settings file to find, once the volume is known to be
// usable (D20). Deliberately after the database work rather than beside the
// configuration wiring above: the directory exists by now, and a template written
// before the schema was proved would be a file left behind by a failed start.
//
// It configures nothing — every key in it is commented out — so it does not
// matter that this run has already read its configuration.
if (!string.IsNullOrEmpty(dataDirectory))
{
    using var scope = app.Services.CreateScope();

    await scope.ServiceProvider
        .GetRequiredService<UserConfigTemplate>()
        .EnsureExistsAsync(dataDirectory, app.Lifetime.ApplicationStopping);
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

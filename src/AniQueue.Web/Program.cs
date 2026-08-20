using AniQueue.Infrastructure;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Persistence.Seeding;
using AniQueue.Infrastructure.Sync;
using AniQueue.Web.Components;
using AniQueue.Web.Services;

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

UserConfigStatus? userConfig = null;

if (!string.IsNullOrEmpty(dataDirectory))
{
    userConfig = new UserConfigStatus
    {
        // Absolute, because the banner naming it is read by someone who has to go
        // and find the file, and a path relative to the content root is not that.
        Path = Path.GetFullPath(Path.Combine(dataDirectory, UserConfigTemplate.FileName))
    };

    // Configured through the source rather than the path overload so that a file
    // which cannot be parsed is survivable. Without OnLoadException the provider
    // throws while the host is being built — before logging exists — so one missing
    // comma in the file an operator edits by hand replaces the application with a
    // stack trace. Ignoring the load leaves every other configuration source in
    // place and lets the application start and say what is wrong (D20).
    //
    // The provider's own load path is used rather than a parse of our own, because
    // the two could disagree about what is acceptable: this file is allowed
    // comments and trailing commas, and the only implementation that defines
    // exactly which is the one doing the reading.
    //
    // This also covers a file broken while the application is running, since a
    // reload failure arrives the same way.
    builder.Configuration.AddJsonFile(source =>
    {
        source.Path = userConfig.Path;
        source.Optional = true;
        source.ReloadOnChange = true;
        source.OnLoadException = context =>
        {
            context.Ignore = true;
            // The innermost exception is the one carrying the line and position. The two
            // wrapping it say only which file, which the banner already names.
            userConfig.Fail(context.Exception.GetBaseException().Message);
        };

        source.ResolveFileProvider();
    });
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

// The timer half of unattended sync (D21). Registered here rather than inside
// AddAniQueueSync because hosting is the web project's business: Infrastructure
// supplies the job, this decides that something runs it.
//
// One runner per job by design, so a slow job can never delay an unrelated one —
// which is the line the relation backfill below was written against, and it cost
// exactly what this comment predicted.
builder.Services.AddHostedService<BackgroundJobRunner<UnattendedSyncJob>>();

// The relation graph (D24), filled in by the first of D25's enrichment passes. Its
// own loop rather than a step inside the sync's, because the two have nothing to
// say to each other: a list changes constantly and relations are near-static, and
// a backfill spreading itself across a rate limit must never be what delays a
// queue advancing.
builder.Services.AddHostedService<BackgroundJobRunner<RelationBackfillJob>>();

// Registered even when the file is fine, so the banner component can ask without
// caring whether a data directory was configured at all.
builder.Services.AddSingleton(userConfig ?? new UserConfigStatus { Path = UserConfigTemplate.FileName });

// Sample data, on request and only in development (D27). Two locks rather than
// one: production never resolves the type, and a development run still has to ask.
//
//     dotnet run --project src/AniQueue.Web -- --SeedSampleData=true
//
// Reading it here rather than inside the seeder keeps the switch where every other
// startup decision is, and keeps the seeder a thing that seeds.
var seedSampleData = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("SeedSampleData");

if (seedSampleData)
{
    builder.Services.AddAniQueueSampleData();
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

    // Said at startup as well as on the page, because the operator who broke the
    // file may be watching a console rather than a browser.
    if (userConfig is { IsBroken: true })
    {
        lifetimeLogger.LogWarning(
            "The settings file at {UserConfigPath} could not be read and was ignored: {Reason}. "
            + "AniQueue started without it; fix the file and restart to apply it",
            userConfig.Path,
            userConfig.Error);
    }
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

    // After the schema and before anything serves, so the first request sees a
    // database that is either empty or complete. Idempotent, and it declines
    // outright if the library already holds anything.
    if (seedSampleData)
    {
        await scope.ServiceProvider
            .GetRequiredService<SampleDataSeeder>()
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

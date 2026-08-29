using AniQueue.Core.Recommendations;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure;
using AniQueue.Infrastructure.Artwork;
using AniQueue.Infrastructure.Jobs;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Persistence.Seeding;
using AniQueue.Infrastructure.Recommendations;
using AniQueue.Infrastructure.Settings;
using AniQueue.Infrastructure.Sync;
using AniQueue.Web.Components;
using AniQueue.Web.Endpoints;
using AniQueue.Web.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Operator settings the self-hoster edits from outside the application, kept
// beside the database in their volume rather than inside the image. The
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
        Path = Path.GetFullPath(Path.Combine(dataDirectory, UserConfigStatus.FileName))
    };

    // Configured through the source rather than the path overload so that a file
    // which cannot be parsed is survivable. Without OnLoadException the provider
    // throws while the host is being built — before logging exists — so one missing
    // comma in the file an operator edits by hand replaces the application with a
    // stack trace. Ignoring the load leaves every other configuration source in
    // place and lets the application start and say what is wrong.
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
    .AddInteractiveServerComponents()

    // Raised from SignalR's 32 KB default, because one of this application's inputs
    // is routinely larger than that and arrives in a single hub message: a model's
    // ranking of a real backlog is tens of kilobytes, and pasting one into a bound
    // textarea sends the whole value at once.
    //
    // Over the default, the circuit is closed rather than the value rejected. The
    // symptom is a page that quietly stops responding — the paste appears to do
    // nothing, the button stays disabled, and nothing anywhere says why. There is no
    // server-side handler that can turn that into a message, which is what makes the
    // limit the wrong place to enforce this.
    //
    // So it is aligned with what the parser will accept (ScoringLimits.MaxBytes).
    // Past that point a reply is refused by code that can say so, in a sentence, on
    // the page. The exposure this trades against is bounded by the same number, on
    // an application that binds to one operator's own network.
    .AddHubOptions(options => options.MaximumReceiveMessageSize = ScoringLimits.Default.MaxBytes);

// Blazor Server signs the antiforgery tokens and circuit identifiers it hands out,
// with keys ASP.NET Core generates on first use. Left where it puts them they live
// in the container's own filesystem and die with it, so recreating the container
// invalidates every page a browser still has open — the symptom is a form that
// reports an antiforgery failure after an upgrade, which reads like a bug in the
// form. Beside the database they are in the volume that already survives a
// recreate, which is the whole point of that volume.
//
// Not created here. The repository creates the directory when the key manager
// first reaches it, which is after the database initialisation below — so an
// unwritable /data still fails with the message that names the real problem.
if (!string.IsNullOrEmpty(dataDirectory))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(
            new DirectoryInfo(Path.GetFullPath(Path.Combine(dataDirectory, "keys"))));
}

// The compose health check's target. Liveness only, and deliberately
// so: a database that cannot be reached already prevents startup below, so a
// check that queried one would be reporting on a state this process cannot be
// in. What it does prove is the only thing a restart policy can act on — that
// the server is accepting requests rather than merely running.
builder.Services.AddHealthChecks();

builder.Services.AddAniQueuePersistence(options =>
    builder.Configuration.GetSection(AniQueueDatabaseOptions.SectionName).Bind(options));

// Bound to the live section rather than through a delegate, unlike the database
// options above: this is the half of configuration an operator edits while the
// application is running, and a section binding is what lets a reload reach the
// options monitor at all.
builder.Services.Configure<SyncOptions>(
    builder.Configuration.GetSection(SyncOptions.SectionName));

// The scoring sizes, on the same terms and for the same reason: how much history a
// model can hold describes that model rather than this application's appearance,
// which is the line between the file and the database.
builder.Services.Configure<ScoringOptions>(
    builder.Configuration.GetSection(ScoringOptions.SectionName));

// One cadence for every background task. Bound to the live section like the
// others, so changing it on the Tasks page reaches a runner without a restart.
builder.Services.Configure<TaskOptions>(
    builder.Configuration.GetSection(TaskOptions.SectionName));

builder.Services.AddAniQueueSync();

// The one reader and writer of userconfig.json. Registered after the section
// bindings above rather than before, because what it writes is what they read.
builder.Services.AddAniQueueSettings();

// The second courier for a ranking. Registered unconditionally even though the
// normal state of a fresh install is no endpoint at all: whether one is configured is
// a question the card asks the service, not one this file answers by leaving it out.
builder.Services.AddAniQueueScoringEndpoint();

// The timer half of unattended sync. Registered here rather than inside
// AddAniQueueSync because hosting is the web project's business: Infrastructure
// supplies the job, this decides that something runs it.
//
// One runner per job by design, so a slow job can never delay an unrelated one —
// which is the line the relation backfill below was written against, and it cost
// exactly what this comment predicted.
builder.Services.AddHostedService<BackgroundJobRunner<UnattendedSyncJob>>();

// The relation graph, filled in by the first enrichment pass. Its
// own loop rather than a step inside the sync's, because the two have nothing to
// say to each other: a list changes constantly and relations are near-static, and
// a backfill spreading itself across a rate limit must never be what delays a
// queue advancing.
builder.Services.AddHostedService<BackgroundJobRunner<RelationBackfillJob>>();

// The third job, and the one that spends somebody's GPU rather than somebody
// else's API budget — so it is off until it is turned on. Its own runner
// like the others: a sweep that runs for an hour must never be what delays a
// queue advancing.
builder.Services.AddHostedService<BackgroundJobRunner<ScoringSweepJob>>();

// The fourth, and the second enrichment pass. Its own runner for
// the same reason as the others, and here that reason is at its sharpest: a first
// pass over a whole library spends several minutes fetching pictures, and a
// picture arriving late must never be what holds up a sync.
builder.Services.AddHostedService<BackgroundJobRunner<CoverArtJob>>();

// Registered even when the file is fine, so the banner component can ask without
// caring whether a data directory was configured at all.
builder.Services.AddSingleton(userConfig ?? new UserConfigStatus { Path = UserConfigStatus.FileName });

// Sample data, on request and only in development. Two locks rather than
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
// usable. Deliberately after the database work rather than beside the
// configuration wiring above: the directory exists by now, and a template written
// before the schema was proved would be a file left behind by a failed start.
//
// It configures nothing — every key in it is commented out — so it does not
// matter that this run has already read its configuration.
if (!string.IsNullOrEmpty(dataDirectory))
{
    await app.Services
        .GetRequiredService<IUserSettingsStore>()
        .EnsureExistsAsync(app.Lifetime.ApplicationStopping);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

// Unauthenticated, and it has to stay that way if a login is ever added: a compose
// health check reaches this before anybody has logged in, and a lock that shuts
// the orchestrator out of the liveness probe is worse than no lock.
app.MapHealthChecks("/health");

app.MapStaticAssets();
app.MapCachedCoverArt();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

/// <summary>Exposed so integration tests and the logger category can name the entry point.</summary>
public partial class Program;

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

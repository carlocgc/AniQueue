using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Persistence;

/// <summary>
/// The application's only database context.
///
/// Instances are short-lived and created through <see cref="IDbContextFactory{TContext}"/>
/// rather than injected as a scoped service (D3). Under Blazor Interactive Server
/// a scoped service lives as long as the SignalR circuit — potentially hours —
/// which would give the change tracker unbounded growth, stale reads, and
/// concurrency failures the first time two components rendered at once.
/// </summary>
public class AniQueueDbContext(DbContextOptions<AniQueueDbContext> options) : DbContext(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<ProfileSettings> ProfileSettings => Set<ProfileSettings>();

    public DbSet<Anime> Anime => Set<Anime>();

    public DbSet<AnimeExternalId> AnimeExternalIds => Set<AnimeExternalId>();

    public DbSet<Franchise> Franchises => Set<Franchise>();

    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();

    public DbSet<QueueItem> QueueItems => Set<QueueItem>();

    public DbSet<RecommendationRun> RecommendationRuns => Set<RecommendationRun>();

    public DbSet<RecommendationRunItem> RecommendationRunItems => Set<RecommendationRunItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // One IEntityTypeConfiguration per entity, discovered by assembly scan, so
        // adding an entity cannot silently skip its indexes and constraints.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AniQueueDbContext).Assembly);
    }
}

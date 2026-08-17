using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class QueueItemConfiguration : IEntityTypeConfiguration<QueueItem>
{
    public void Configure(EntityTypeBuilder<QueueItem> builder)
    {
        builder.HasKey(q => q.Id);

        // Ordering lookups. Deliberately NOT unique (D2): SQLite evaluates
        // uniqueness per statement rather than at commit, so a reorder that shifts
        // a block of positions would collide against itself mid-transaction and
        // abort. Contiguity is a QueueService invariant covered by tests instead.
        builder
            .HasIndex(q => new { q.ProfileId, q.Position })
            .HasDatabaseName("IX_QueueItems_ProfileId_Position");

        // The same title must not occupy two slots. No longer filtered, because
        // AnimeId is no longer nullable — a slot is always exactly one title (D15).
        builder
            .HasIndex(q => new { q.ProfileId, q.AnimeId })
            .IsUnique()
            .HasDatabaseName("IX_QueueItems_ProfileId_AnimeId");

        builder
            .HasOne(q => q.Anime)
            .WithMany()
            .HasForeignKey(q => q.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(q => q.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

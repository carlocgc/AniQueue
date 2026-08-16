using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class QueueItemConfiguration : IEntityTypeConfiguration<QueueItem>
{
    public void Configure(EntityTypeBuilder<QueueItem> builder)
    {
        builder.HasKey(q => q.Id);

        builder.Ignore(q => q.IsFranchise);

        // Ordering lookups. Deliberately NOT unique (D2): SQLite evaluates
        // uniqueness per statement rather than at commit, so a reorder that shifts
        // a block of positions would collide against itself mid-transaction and
        // abort. Contiguity is a QueueService invariant covered by tests instead.
        builder
            .HasIndex(q => new { q.ProfileId, q.Position })
            .HasDatabaseName("IX_QueueItems_ProfileId_Position");

        // The same title or franchise must not occupy two slots. Filtered because
        // exactly one of the two columns is null in every row.
        builder
            .HasIndex(q => new { q.ProfileId, q.AnimeId })
            .IsUnique()
            .HasFilter("\"AnimeId\" IS NOT NULL")
            .HasDatabaseName("IX_QueueItems_ProfileId_AnimeId");

        builder
            .HasIndex(q => new { q.ProfileId, q.FranchiseId })
            .IsUnique()
            .HasFilter("\"FranchiseId\" IS NOT NULL")
            .HasDatabaseName("IX_QueueItems_ProfileId_FranchiseId");

        builder
            .HasOne(q => q.Anime)
            .WithMany()
            .HasForeignKey(q => q.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(q => q.Franchise)
            .WithMany()
            .HasForeignKey(q => q.FranchiseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(q => q.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        // A slot is exactly one thing: one anime or one franchise, never both and
        // never neither (D1). "<>" is XOR over the two null-ness tests.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_QueueItems_AnimeXorFranchise",
            "(\"AnimeId\" IS NULL) <> (\"FranchiseId\" IS NULL)"));
    }
}

using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class AnimeConfiguration : IEntityTypeConfiguration<Anime>
{
    public void Configure(EntityTypeBuilder<Anime> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Title).IsRequired().HasMaxLength(500);
        builder.Property(a => a.AlternativeTitle).HasMaxLength(500);
        builder.Property(a => a.CoverImageUrl).HasMaxLength(2000);
        builder.Property(a => a.SourceAnimeId).HasMaxLength(64);

        builder.HasIndex(a => a.Title);

        // Deduplication key for imports. Filtered because manual entries have no74
        // SourceAnimeId: without the filter, every manual entry would collide with
        // every other one on (Manual, NULL).
        builder
            .HasIndex(a => new { a.Source, a.SourceAnimeId })
            .IsUnique()
            .HasFilter("\"SourceAnimeId\" IS NOT NULL")
            .HasDatabaseName("IX_Anime_Source_SourceAnimeId");

        builder.HasIndex(a => a.FranchiseId);

        builder
            .HasOne(a => a.Franchise)
            .WithMany(f => f.Entries)
            .HasForeignKey(a => a.FranchiseId)
            // Dissolving a franchise must not delete the titles in it; they simply
            // become standalone again.
            .OnDelete(DeleteBehavior.SetNull);
    }
}

using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class RecommendationRunItemConfiguration : IEntityTypeConfiguration<RecommendationRunItem>
{
    public void Configure(EntityTypeBuilder<RecommendationRunItem> builder)
    {
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Reason).HasMaxLength(2000);

        builder.HasIndex(i => new { i.RunId, i.Rank });

        builder
            .HasOne(i => i.Anime)
            .WithMany()
            .HasForeignKey(i => i.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(i => i.Franchise)
            .WithMany()
            .HasForeignKey(i => i.FranchiseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Same exclusive-or rule as QueueItem: a ranked candidate is one thing.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RecommendationRunItems_AnimeXorFranchise",
            "(\"AnimeId\" IS NULL) <> (\"FranchiseId\" IS NULL)"));

        // These arrive from an external model. Range-checking at the database
        // boundary means a validation gap upstream cannot persist nonsense.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RecommendationRunItems_ConfidenceRange",
            "\"Confidence\" >= 0.0 AND \"Confidence\" <= 1.0"));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RecommendationRunItems_RankPositive",
            "\"Rank\" >= 1"));
    }
}

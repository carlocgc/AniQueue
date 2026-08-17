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

        // No franchise reference and no exclusive-or constraint: a ranked candidate
        // is always one title (D16). A franchise placement had nowhere to be applied
        // to, because applying a run caches onto LibraryEntry and a franchise has no
        // LibraryEntry row.

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

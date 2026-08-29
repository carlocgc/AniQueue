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


        builder
            .HasOne(i => i.Anime)
            .WithMany()
            .HasForeignKey(i => i.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        // These arrive from an external model. Range-checking at the database
        // boundary means a validation gap upstream cannot persist nonsense.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RecommendationRunItems_ConfidenceRange",
            "\"Confidence\" >= 0.0 AND \"Confidence\" <= 1.0"));

        // CK_RecommendationRunItems_RankPositive went with the column. Its
        // presence is why dropping Rank needs a table rebuild rather than an ALTER:
        // SQLite will not drop a column from a table carrying a check constraint.
    }
}

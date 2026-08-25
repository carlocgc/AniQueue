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

        // No (RunId, Rank) index any more: D43 deleted the column it ordered by. Not
        // declared here because EF creates a plain IX_RecommendationRunItems_RunId
        // for the foreign key on its own, and the composite was only ever that index
        // with a sort key bolted on — declaring one would be a duplicate.

        builder
            .HasOne(i => i.Anime)
            .WithMany()
            .HasForeignKey(i => i.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        // No group reference and no exclusive-or constraint: a ranked candidate is
        // always one title (D16). A group's placement had nowhere to be applied to,
        // because applying a run caches onto LibraryEntry and a group had no
        // LibraryEntry row — and since D23 there are no groups at all.

        // These arrive from an external model. Range-checking at the database
        // boundary means a validation gap upstream cannot persist nonsense.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_RecommendationRunItems_ConfidenceRange",
            "\"Confidence\" >= 0.0 AND \"Confidence\" <= 1.0"));

        // CK_RecommendationRunItems_RankPositive went with the column (D43). Its
        // presence is why dropping Rank needs a table rebuild rather than an ALTER:
        // SQLite will not drop a column from a table carrying a check constraint.
    }
}

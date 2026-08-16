using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class LibraryEntryConfiguration : IEntityTypeConfiguration<LibraryEntry>
{
    public void Configure(EntityTypeBuilder<LibraryEntry> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PersonalNotes).HasMaxLength(8000);
        builder.Property(e => e.RecommendationReason).HasMaxLength(2000);

        // A profile has exactly one relationship with a given title. This is what
        // makes re-importing the same export idempotent rather than duplicating.
        builder
            .HasIndex(e => new { e.ProfileId, e.AnimeId })
            .IsUnique()
            .HasDatabaseName("IX_LibraryEntries_ProfileId_AnimeId");

        builder.HasIndex(e => new { e.ProfileId, e.Status });
        builder.HasIndex(e => new { e.ProfileId, e.IsHidden });

        // Backlog views sort by AI score; without this the sort is a full scan.
        builder.HasIndex(e => new { e.ProfileId, e.RecommendationScore });

        builder
            .HasOne(e => e.Anime)
            .WithMany()
            .HasForeignKey(e => e.AnimeId)
            // Removing a title removes the user's relationship with it; leaving an
            // orphaned entry pointing at nothing would be meaningless.
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(e => e.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        // A 1-10 rating, or nothing. Guards against an import writing MAL's "0
        // means unscored" convention straight through as a real score.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_LibraryEntries_UserScoreRange",
            "\"UserScore\" IS NULL OR (\"UserScore\" >= 1 AND \"UserScore\" <= 10)"));
    }
}

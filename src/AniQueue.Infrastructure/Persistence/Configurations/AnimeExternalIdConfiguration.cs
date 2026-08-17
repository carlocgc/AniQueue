using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class AnimeExternalIdConfiguration : IEntityTypeConfiguration<AnimeExternalId>
{
    public void Configure(EntityTypeBuilder<AnimeExternalId> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExternalId).IsRequired().HasMaxLength(64);

        // The deduplication key for every import and sync, and deliberately
        // *unfiltered* — unlike the index it replaces. That one needed
        // "WHERE SourceAnimeId IS NOT NULL" because a manual entry stored a null
        // identifier and would otherwise collide with every other manual entry on
        // (Manual, NULL). A manual entry now has no row here at all, so the null
        // case the filter existed for cannot occur (D17).
        builder
            .HasIndex(x => new { x.Source, x.ExternalId })
            .IsUnique()
            .HasDatabaseName("IX_AnimeExternalIds_Source_ExternalId");

        // A title has at most one identifier per source. Nothing legitimately
        // issues two MyAnimeList ids for one show, so a second is evidence that
        // two sources disagree about identity — which is a conflict for the user
        // to resolve, not a row to write. Enforced here so it cannot be silently
        // stored while the matching path is being extended for AniList.
        builder
            .HasIndex(x => new { x.AnimeId, x.Source })
            .IsUnique()
            .HasDatabaseName("IX_AnimeExternalIds_AnimeId_Source");

        builder
            .HasOne(x => x.Anime)
            .WithMany(a => a.ExternalIds)
            .HasForeignKey(x => x.AnimeId)
            // Identity is meaningless without the title it identifies.
            .OnDelete(DeleteBehavior.Cascade);
    }
}

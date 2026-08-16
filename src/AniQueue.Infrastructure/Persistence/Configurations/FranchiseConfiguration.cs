using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class FranchiseConfiguration : IEntityTypeConfiguration<Franchise>
{
    public void Configure(EntityTypeBuilder<Franchise> builder)
    {
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name).IsRequired().HasMaxLength(300);
        builder.Property(f => f.Description).HasMaxLength(4000);

        // Indexed but not unique: two franchises may legitimately share a name
        // (different adaptations of the same source), and blocking that would be
        // a surprising failure during manual curation.
        builder.HasIndex(f => f.Name);

        builder.HasIndex(f => f.ManualSortOrder);
    }
}

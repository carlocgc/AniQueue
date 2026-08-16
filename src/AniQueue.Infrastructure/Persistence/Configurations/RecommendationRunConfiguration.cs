using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class RecommendationRunConfiguration : IEntityTypeConfiguration<RecommendationRun>
{
    public void Configure(EntityTypeBuilder<RecommendationRun> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ProviderName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ModelIdentifier).HasMaxLength(200);

        // History is browsed newest-first.
        builder.HasIndex(r => new { r.ProfileId, r.CreatedAt });

        builder
            .HasOne<Profile>()
            .WithMany()
            .HasForeignKey(r => r.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasMany(r => r.Items)
            .WithOne(i => i.Run)
            .HasForeignKey(i => i.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class SourceSyncSettingsConfiguration : IEntityTypeConfiguration<SourceSyncSettings>
{
    public void Configure(EntityTypeBuilder<SourceSyncSettings> builder)
    {
        builder.HasKey(s => s.Id);

        // One configuration per profile per source. This is the key that made the
        // entity separate from ProfileSettings rather than more columns on it (D20).
        builder
            .HasIndex(s => new { s.ProfileId, s.Source })
            .IsUnique()
            .HasDatabaseName("IX_SourceSyncSettings_ProfileId_Source");

        builder
            .HasOne(s => s.Profile)
            .WithMany()
            .HasForeignKey(s => s.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

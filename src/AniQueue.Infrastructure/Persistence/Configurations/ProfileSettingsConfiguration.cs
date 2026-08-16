using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class ProfileSettingsConfiguration : IEntityTypeConfiguration<ProfileSettings>
{
    public void Configure(EntityTypeBuilder<ProfileSettings> builder)
    {
        builder.HasKey(s => s.Id);

        builder.Property(s => s.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(s => s.DateFormat).IsRequired().HasMaxLength(50);

        // Exactly one settings row per profile.
        builder.HasIndex(s => s.ProfileId).IsUnique();
    }
}

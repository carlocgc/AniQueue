using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(100);

        // Twelve characters (D50), with room to spare rather than a tight fit: the
        // column is compared and never parsed, so a longer value from a future build
        // would be wrong rather than truncated.
        builder.Property(p => p.LibraryKey).HasMaxLength(32);

        builder.HasIndex(p => p.Name).IsUnique();

        builder
            .HasOne(p => p.Settings)
            .WithOne(s => s.Profile)
            .HasForeignKey<ProfileSettings>(s => s.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

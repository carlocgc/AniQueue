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

        // Twelve characters, with room to spare rather than a tight fit: the
        // column is compared and never parsed, so a longer value from a future build
        // would be wrong rather than truncated.
        builder.Property(p => p.LibraryKey).HasMaxLength(32);

        // Sized to the format rather than to the value: a stored password is
        // version, work factor and two base64 fields, and a later format with a
        // higher cost would be longer. The stamp is a GUID without its hyphens.
        builder.Property(p => p.PasswordHash).HasMaxLength(256);

        builder.Property(p => p.SecurityStamp).HasMaxLength(64);

        builder.HasIndex(p => p.Name).IsUnique();

        builder
            .HasOne(p => p.Settings)
            .WithOne(s => s.Profile)
            .HasForeignKey<ProfileSettings>(s => s.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

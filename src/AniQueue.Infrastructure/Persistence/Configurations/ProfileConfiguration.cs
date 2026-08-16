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

        builder.HasIndex(p => p.Name).IsUnique();

        builder
            .HasOne(p => p.Settings)
            .WithOne(s => s.Profile)
            .HasForeignKey<ProfileSettings>(s => s.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

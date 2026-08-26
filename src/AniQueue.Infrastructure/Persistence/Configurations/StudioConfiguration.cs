using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class StudioConfiguration : IEntityTypeConfiguration<Studio>
{
    public void Configure(EntityTypeBuilder<Studio> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);

        builder.HasIndex(x => x.Name).IsUnique();
    }
}

public class AnimeStudioConfiguration : IEntityTypeConfiguration<AnimeStudio>
{
    public void Configure(EntityTypeBuilder<AnimeStudio> builder)
    {
        // A title credits a company once. IsMain is an attribute of that one
        // pairing rather than a second pairing, which is what makes this a join
        // entity and AnimeGenre a pure join (D49).
        builder.HasKey(x => new { x.AnimeId, x.StudioId });

        builder
            .HasOne(x => x.Anime)
            .WithMany(a => a.Studios)
            .HasForeignKey(x => x.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(x => x.Studio)
            .WithMany(s => s.Anime)
            .HasForeignKey(x => x.StudioId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.StudioId);
    }
}

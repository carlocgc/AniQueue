using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class AnimeConfiguration : IEntityTypeConfiguration<Anime>
{
    public void Configure(EntityTypeBuilder<Anime> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Title).IsRequired().HasMaxLength(500);
        builder.Property(a => a.TitleRomaji).HasMaxLength(500);
        builder.Property(a => a.TitleEnglish).HasMaxLength(500);
        builder.Property(a => a.TitleNative).HasMaxLength(500);
        builder.Property(a => a.CoverImageUrl).HasMaxLength(2000);

        builder.HasIndex(a => a.Title);

        // No index over (Source, SourceAnimeId) any more. Deduplication is keyed on
        // AnimeExternalId now (D17), because one column could only ever hold one
        // identity — and a library imported from one service then synced from
        // another matched nothing and conflicted on every title.
    }
}

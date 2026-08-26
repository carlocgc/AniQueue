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

        // Six hexadecimal digits behind a hash, and nothing else is ever stored.
        builder.Property(a => a.CoverImageColor).HasMaxLength(7);

        builder.HasIndex(a => a.Title);

        // No CoverImageUrl any more (D47). Where a picture lives is a fact about the
        // picture, and a title has more than one, so it is a row on AnimeImage. The
        // column could hold one address — and, being written through the import
        // merge, could not be changed to a different one without a data migration
        // nobody would have known to write.

        // No index over (Source, SourceAnimeId) any more. Deduplication is keyed on
        // AnimeExternalId now (D17), because one column could only ever hold one
        // identity — and a library imported from one service then synced from
        // another matched nothing and conflicted on every title.
    }
}

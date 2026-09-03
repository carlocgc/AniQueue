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

        // Sized the way the import limit and ImageSource.MaxByteCount are:
        // generously enough that nothing legitimate is refused — AniList's longest
        // synopses run to a couple of thousand characters — and bounded so a
        // malformed or hostile response cannot write unbounded text a row at a time.
        // SQLite does not enforce this, which is exactly why it is stated: the cap
        // documents the contract the provider does not.
        builder.Property(a => a.Description).HasMaxLength(8000);

        builder.HasIndex(a => a.Title);

        // No CoverImageUrl any more. Where a picture lives is a fact about the
        // picture, and a title has more than one, so it is a row on AnimeImage. The
        // column could hold one address — and, being written through the import
        // merge, could not be changed to a different one without a data migration
        // nobody would have known to write.

        // No index over (Source, SourceAnimeId) any more. Deduplication is keyed on
        // AnimeExternalId now, because one column could only ever hold one
        // identity — and a library imported from one service then synced from
        // another matched nothing and conflicted on every title.
    }
}

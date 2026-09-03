using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class GenreConfiguration : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);

        // The vocabulary is small and closed, so this index is what makes "does this
        // genre already exist" a lookup rather than a scan on every title of every
        // sync — the question is asked once per genre per entry, not once per sync.
        builder.HasIndex(x => x.Name).IsUnique();
    }
}

public class AnimeGenreConfiguration : IEntityTypeConfiguration<AnimeGenre>
{
    public void Configure(EntityTypeBuilder<AnimeGenre> builder)
    {
        builder.HasKey(x => new { x.AnimeId, x.GenreId });

        builder
            .HasOne(x => x.Anime)
            .WithMany(a => a.Genres)
            .HasForeignKey(x => x.AnimeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(x => x.Genre)
            .WithMany(g => g.Anime)
            .HasForeignKey(x => x.GenreId)
            // Restrict rather than Cascade, because deleting a genre is not something
            // this application does and an accidental one should fail loudly instead
            // of quietly unlabelling every title carrying it.
            .OnDelete(DeleteBehavior.Restrict);

        // The reverse of the composite key, for the query this table exists to make
        // possible later: every title carrying one genre, indexed and server-side.
        // Nothing runs it yet — the dialog renders chips from a title it already
        // has — and it is here because it is the half of the key an index cannot be
        // derived from.
        builder.HasIndex(x => x.GenreId);
    }
}

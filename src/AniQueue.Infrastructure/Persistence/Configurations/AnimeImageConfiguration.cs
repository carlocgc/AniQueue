using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class AnimeImageConfiguration : IEntityTypeConfiguration<AnimeImage>
{
    public void Configure(EntityTypeBuilder<AnimeImage> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.RemoteUrl).IsRequired().HasMaxLength(2000);

        // Long enough for a SHA-256 in hex, and it is a hash rather than a name, so
        // it is fixed width by construction.
        builder.Property(x => x.ContentHash).HasMaxLength(64);

        builder.Property(x => x.FetchedUrl).HasMaxLength(2000);

        builder.Property(x => x.FileExtension).HasMaxLength(8);

        // One picture of each kind, at each size, from each source. What gives a
        // title two rows in practice is rendition — the thumbnail a list slot wants
        // and the full-size cover the detail dialog wants. One poster per source per
        // size is what keeps a title from accumulating a new row on every fetch
        // when a source changes its URL.
        builder
            .HasIndex(x => new { x.AnimeId, x.Kind, x.Source, x.Rendition })
            .IsUnique()
            .HasDatabaseName("IX_AnimeImages_AnimeId_Kind_Source_Rendition");

        // No index for the job's own query, deliberately. What it looks for is
        // "FetchedUrl and RemoteUrl disagree", which is a comparison between two
        // columns and not something an index or a filtered index can express. It is a
        // scan over one row per title per kind, in a background job, on a database
        // of several thousand titles, which is cheaper than the index that
        // could not answer the question anyway.

        builder
            .HasOne(x => x.Anime)
            .WithMany(a => a.Images)
            .HasForeignKey(x => x.AnimeId)
            // A picture of a title that is gone is a file nothing can ever ask for.
            // The cached file outlives the row by design; the job's orphan sweep is
            // what removes it, because a delete here cannot reach the filesystem.
            .OnDelete(DeleteBehavior.Cascade);
    }
}

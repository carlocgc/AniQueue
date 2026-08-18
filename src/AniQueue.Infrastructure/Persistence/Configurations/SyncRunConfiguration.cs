using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.HasKey(r => r.Id);

        // Long enough for an explanation, short enough that nothing can dump a
        // response body into it.
        builder.Property(r => r.FailureReason).HasMaxLength(500);

        // Every read of this table is "the most recent run for this source", which
        // is what the Sources page asks on every render.
        //
        // Deliberately not indexed on StartedAt, because nothing orders by it:
        // SQLite cannot sort a DateTimeOffset — it is stored as text with an offset —
        // so recency is read from the key instead, which for an insert-only table is
        // the same order and needs no index of its own.
        builder
            .HasIndex(r => new { r.ProfileId, r.Source })
            .HasDatabaseName("IX_SyncRuns_ProfileId_Source");

        builder
            .HasOne(r => r.Profile)
            .WithMany()
            .HasForeignKey(r => r.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

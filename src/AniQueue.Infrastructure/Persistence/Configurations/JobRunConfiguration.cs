using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        builder.HasKey(r => r.Id);

        // Short because they are identifiers rather than prose: a task key is a
        // constant in code and a unit key is a source name.
        builder.Property(r => r.TaskKey).HasMaxLength(64).IsRequired();
        builder.Property(r => r.UnitKey).HasMaxLength(64).IsRequired();

        // Long enough for an explanation, short enough that nothing can dump a
        // response body into it — the same bound SyncRun uses, for the same reason.
        builder.Property(r => r.FailureReason).HasMaxLength(500);

        // Every read is "the runs of this unit, newest first": the due check takes the
        // first, the history takes a page of them, and pruning deletes below a key.
        // All three are this index.
        //
        // Deliberately not indexed on StartedAt, because nothing orders by it. SQLite
        // cannot sort a DateTimeOffset — it is stored as text with an offset — so
        // recency is read from the key, which for an insert-only table is the same
        // order and needs no index of its own.
        builder
            .HasIndex(r => new { r.TaskKey, r.UnitKey, r.Id })
            .HasDatabaseName("IX_JobRuns_TaskKey_UnitKey_Id");

        // No relationship to Profile, and no ProfileId to hang one on: a background
        // task belongs to the deployment rather than to a profile.
    }
}

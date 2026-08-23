using AniQueue.Core.Domain;
using AniQueue.Core.Jobs;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The run record without a database, for the job suites that only care what it
/// answers.
/// </summary>
/// <remarks>
/// A hand-written fake rather than a mock, per the suite's convention, and shared
/// because both job suites ask it the same question: when did this unit last run.
/// What it stores is exercised for real against SQLite in <c>JobRunStoreTests</c>,
/// where the pruning and the ordering are the behaviour under test.
/// </remarks>
internal sealed class FakeJobRunStore : IJobRunStore
{
    /// <summary>What the due check will be told. Null means "never run".</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    public List<JobRun> Recorded { get; } = [];

    public Task RecordAsync(JobRun run, CancellationToken cancellationToken = default)
    {
        Recorded.Add(run);
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> LastRunAtAsync(
        string taskKey,
        string? unitKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LastRunAt);
}

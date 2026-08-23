namespace AniQueue.Core.Jobs;

/// <summary>
/// The runner's half of <see cref="ITaskRegistry"/>: waiting for a request, and
/// saying what is happening (D40).
/// </summary>
/// <remarks>
/// Separated from the page's half because the two halves are used by things that
/// should not be able to do each other's job. A page must not be able to say "a run
/// started", and a runner has no business enumerating rows. Both are implemented by
/// one object, because it is one piece of state.
/// </remarks>
public interface ITaskRunnerBridge
{
    /// <summary>
    /// Waits until somebody asks for one of this task's units to run.
    /// </summary>
    /// <returns>The unit key that was asked for; null means the task's only unit.</returns>
    /// <remarks>
    /// One outstanding wait per runner, awaited alongside the timer and the
    /// library-change signal. A request that arrives while a run is in flight is held
    /// until the loop comes round, which is what keeps <i>Run now</i> inside the
    /// sequential loop instead of beside it.
    /// </remarks>
    Task<string?> WaitForRequestAsync(string taskKey, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a unit as running and hands back the token its work should observe.
    /// </summary>
    /// <remarks>
    /// The token is linked to the one passed in, so shutdown still stops everything;
    /// what it adds is a second way to trip it that belongs to this unit alone.
    /// Disposing the scope marks the unit idle again and releases the source.
    /// </remarks>
    ITaskRunScope BeginRun(string taskKey, string? unitKey, CancellationToken stoppingToken);
}

/// <summary>A run in flight, for as long as it is held.</summary>
public interface ITaskRunScope : IDisposable
{
    /// <summary>What the work should observe: shutdown, or this unit being cancelled.</summary>
    CancellationToken Token { get; }

    /// <summary>
    /// Whether this unit was cancelled, as opposed to the application stopping.
    /// </summary>
    /// <remarks>
    /// The distinction the runner needs and cannot otherwise make: both arrive as a
    /// tripped token, and only one of them is a run worth recording as cancelled.
    /// Recording a shutdown that way would fill the history with rows every time the
    /// container restarted.
    /// </remarks>
    bool WasCancelled { get; }
}

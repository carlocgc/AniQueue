namespace AniQueue.Core.Jobs;

/// <summary>One task as the page sees it right now.</summary>
/// <param name="TaskKey">What its history is filed under.</param>
/// <param name="UnitKey">Which unit within the task, or null where there is one.</param>
/// <param name="Name">What to call it on the row.</param>
/// <param name="StartedAt">When the run in flight began, or null when it is idle.</param>
public sealed record TaskState(string TaskKey, string? UnitKey, string Name, DateTimeOffset? StartedAt)
{
    public bool IsRunning => StartedAt is not null;
}

/// <summary>
/// What is running, what can be started, and what can be stopped.
///
/// The only thing the tasks page talks to. It never reaches a job: a run requested
/// here is delivered to the runner that owns the job, which starts it inside its own
/// sequential loop — so <i>Run now</i> cannot produce a second run beside a tick, and
/// "ticks cannot overlap" stays structural rather than becoming something the page
/// has to be careful about.
/// </summary>
/// <remarks>
/// <b>A singleton with two sides.</b> Runners register their units, wait here for
/// requests, and report when a run starts and stops; pages read the state and ask for
/// runs. Neither knows the other exists, which is what lets a page be opened and
/// closed without a job noticing.
///
/// <b>In memory, deliberately.</b> What is running now cannot outlive the process
/// that is running it — a restart means nothing is running, which is true rather than
/// forgotten. What survives a restart is the history, and that is
/// <see cref="IJobRunStore"/>'s business.
/// </remarks>
public interface ITaskRegistry
{
    /// <summary>
    /// Raised whenever a row changes: a run starting, finishing, or being asked for.
    /// </summary>
    /// <remarks>
    /// Handlers run on whatever thread changed the state, which is never the render
    /// thread — a component subscribing must marshal with <c>InvokeAsync</c> and must
    /// unsubscribe when it is disposed. A singleton holding a reference to a disposed
    /// component is a leak that survives every navigation.
    /// </remarks>
    event Action? Changed;

    /// <summary>Every task's every unit, in registration order.</summary>
    IReadOnlyList<TaskState> Snapshot();

    /// <summary>
    /// Announces a job's units, so they have rows before anything has ever run.
    /// </summary>
    /// <remarks>
    /// Called by the runner at startup rather than composed from DI, because the unit
    /// list comes from a job resolved in a scope and is allowed to depend on
    /// configuration. A task nobody registered simply has no row, which is what
    /// happens to a job that is not hosted.
    /// </remarks>
    void Register(string taskKey, IReadOnlyList<JobUnit> units);

    /// <summary>
    /// Asks for a unit to run as soon as its runner is free.
    /// </summary>
    /// <remarks>
    /// Returns false when the unit is unknown. It deliberately does <b>not</b> refuse
    /// while a run is in flight: the request is queued to the runner's loop, which
    /// starts it when the current tick finishes. What the button means is "as soon as
    /// you can", and saying so is the page's job rather than this one's.
    /// </remarks>
    bool RequestRun(string taskKey, string? unitKey);

    /// <summary>
    /// Stops the run in flight for this unit, if there is one.
    /// </summary>
    /// <remarks>
    /// Cooperative: the token is tripped and the job stops at its next safe point —
    /// between batches, between requests, before a commit. Returns false when nothing
    /// was running, which is the normal answer to a second press.
    /// </remarks>
    bool Cancel(string taskKey, string? unitKey);
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using AniQueue.Core.Jobs;

namespace AniQueue.Infrastructure.Jobs;

/// <summary>
/// The rendezvous between the tasks page and the runners.
/// </summary>
/// <remarks>
/// A singleton, like <c>ILibraryChangeNotifier</c> and for the same reason: it joins
/// a background loop to an open circuit, and neither could reach the other otherwise.
/// </remarks>
public sealed class TaskRegistry : ITaskRegistry, ITaskRunnerBridge
{
    private readonly ConcurrentDictionary<(string Task, string Unit), Entry> _units = new();

    /// <summary>
    /// Requests waiting for a runner, one queue per task.
    /// </summary>
    /// <remarks>
    /// A channel rather than a semaphore, because <i>which</i> unit was asked for has
    /// to survive the wait — a task with two sources cannot answer "somebody pressed
    /// something".
    ///
    /// Bounded at one per unit by <see cref="Entry.Requested"/> rather than by the
    /// channel's capacity: pressing a button twice while a run is queued should mean
    /// one run, and a bounded channel would either block the caller or drop the
    /// request without either side knowing which.
    /// </remarks>
    private readonly ConcurrentDictionary<string, Channel<string?>> _requests = new();

    public event Action? Changed;

    /// <summary>
    /// Every unit, in a stable order.
    /// </summary>
    /// <remarks>
    /// <b>By key rather than by registration.</b> Registration order was the obvious
    /// choice and is not stable: each job has its own hosted service and they start
    /// concurrently, so the rows came out in a different order on almost every boot.
    /// A page whose rows move when nothing has changed is a page nobody trusts, and
    /// the fix is not to make startup deterministic — it is to stop depending on it.
    ///
    /// Sorting by name instead would move a row when somebody renamed a task, which is
    /// the same problem arriving more slowly. The key is what does not change.
    /// </remarks>
    public IReadOnlyList<TaskState> Snapshot() =>
        [.. _units.Values
            .OrderBy(entry => entry.TaskKey, StringComparer.Ordinal)
            .ThenBy(entry => entry.UnitKey ?? string.Empty, StringComparer.Ordinal)
            .Select(entry => new TaskState(
                entry.TaskKey,
                entry.UnitKey,
                entry.Name,
                entry.StartedAt))];

    public void Register(string taskKey, IReadOnlyList<JobUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        foreach (var unit in units)
        {
            _units[(taskKey, unit.Key ?? string.Empty)] = new Entry(taskKey, unit.Key, unit.Name);
        }

        Notify();
    }

    public bool RequestRun(string taskKey, string? unitKey)
    {
        if (!_units.TryGetValue((taskKey, unitKey ?? string.Empty), out var entry))
        {
            return false;
        }

        // One outstanding request per unit. Pressing twice while one is queued is the
        // same intent expressed twice, and running twice is not what was meant.
        if (Interlocked.Exchange(ref entry.Requested, 1) == 1)
        {
            return true;
        }

        Queue(taskKey).Writer.TryWrite(unitKey);
        Notify();

        return true;
    }

    public bool Cancel(string taskKey, string? unitKey)
    {
        if (!_units.TryGetValue((taskKey, unitKey ?? string.Empty), out var entry))
        {
            return false;
        }

        var running = entry.Running;

        if (running is null)
        {
            return false;
        }

        running.Cancel();

        return true;
    }

    public async Task<string?> WaitForRequestAsync(string taskKey, CancellationToken cancellationToken) =>
        await Queue(taskKey).Reader.ReadAsync(cancellationToken);

    public ITaskRunScope BeginRun(string taskKey, string? unitKey, CancellationToken stoppingToken)
    {
        var entry = _units.GetOrAdd(
            (taskKey, unitKey ?? string.Empty),
            _ => new Entry(taskKey, unitKey, unitKey ?? taskKey));

        // Cleared as the run starts rather than as it is dequeued, so a request made
        // while this one is in flight is a fresh one and gets its own run.
        Interlocked.Exchange(ref entry.Requested, 0);

        var scope = new RunScope(this, entry, stoppingToken);

        entry.StartedAt = DateTimeOffset.UtcNow;
        entry.Running = scope;

        Notify();

        return scope;
    }

    private Channel<string?> Queue(string taskKey) =>
        _requests.GetOrAdd(taskKey, _ => Channel.CreateUnbounded<string?>(new UnboundedChannelOptions
        {
            SingleReader = true
        }));

    private void Notify()
    {
        // Each handler on its own, because one page failing to accept a change must
        // not stop the others hearing it.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // A page that cannot accept this is a page problem. There is nothing
                // useful to do here and no logger worth taking a dependency on for it.
            }
        }
    }

    /// <summary>One unit's mutable state.</summary>
    private sealed class Entry(string taskKey, string? unitKey, string name)
    {
        /// <summary>Read and written with Interlocked, hence a field.</summary>
        public int Requested;

        public string TaskKey { get; } = taskKey;

        public string? UnitKey { get; } = unitKey;

        public string Name { get; } = name;

        public DateTimeOffset? StartedAt { get; set; }

        public RunScope? Running { get; set; }
    }

    private sealed class RunScope : ITaskRunScope
    {
        private readonly TaskRegistry _registry;
        private readonly Entry _entry;
        private readonly CancellationTokenSource _source;

        public RunScope(TaskRegistry registry, Entry entry, CancellationToken stoppingToken)
        {
            _registry = registry;
            _entry = entry;
            _source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        }

        public CancellationToken Token => _source.Token;

        public bool WasCancelled { get; private set; }

        public void Cancel()
        {
            // Set before tripping the token, so whoever observes the cancellation can
            // already tell it apart from a shutdown.
            WasCancelled = true;

            try
            {
                _source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the button and here. Nothing to stop.
            }
        }

        public void Dispose()
        {
            _entry.StartedAt = null;
            _entry.Running = null;

            _source.Dispose();

            _registry.Notify();
        }
    }
}

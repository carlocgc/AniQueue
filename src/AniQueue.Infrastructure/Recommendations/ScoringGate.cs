using AniQueue.Core.Recommendations;

namespace AniQueue.Infrastructure.Recommendations;

/// <summary>
/// One model, one request at a time, and the waiting person counted (D31).
/// </summary>
/// <remarks>
/// A singleton, because it is a rendezvous between things with no other way to reach
/// each other — a background scope running a sweep, and whichever circuit pressed
/// <i>Rank now</i>. The same argument <c>LibraryChangeNotifier</c> makes.
/// </remarks>
public sealed class ScoringGate : IScoringGate, IDisposable
{
    private readonly SemaphoreSlim _model = new(1, 1);

    private int _interactiveWaiting;

    public bool IsInteractiveWaiting => Volatile.Read(ref _interactiveWaiting) > 0;

    public async Task<IDisposable> EnterInteractiveAsync(CancellationToken cancellationToken = default)
    {
        // Counted before waiting, not after acquiring. The sweep reads this to decide
        // whether to stand down, and somebody who has already got the model does not
        // need it to — the whole value of the flag is in the interval where they are
        // still queued behind a batch.
        Interlocked.Increment(ref _interactiveWaiting);

        try
        {
            await _model.WaitAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref _interactiveWaiting);
            throw;
        }

        Interlocked.Decrement(ref _interactiveWaiting);

        return new Release(_model);
    }

    public async Task<IDisposable> EnterSweepAsync(CancellationToken cancellationToken = default)
    {
        await _model.WaitAsync(cancellationToken);

        return new Release(_model);
    }

    public void Dispose() => _model.Dispose();

    /// <summary>Releases exactly once, however many times it is disposed.</summary>
    /// <remarks>
    /// A second release would raise the semaphore's count above one and let two
    /// requests through at once — the failure this type exists to prevent, arriving
    /// through the type itself.
    /// </remarks>
    private sealed class Release(SemaphoreSlim model) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                model.Release();
            }
        }
    }
}

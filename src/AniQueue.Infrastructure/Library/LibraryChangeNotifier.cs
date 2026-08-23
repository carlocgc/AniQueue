using AniQueue.Core.Library;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Library;

/// <summary>
/// The one instance every page subscribes to and every background job publishes
/// through.
/// </summary>
/// <remarks>
/// <b>A throwing subscriber must not stop the others, or the job.</b> Handlers here
/// are open Blazor circuits, and a circuit can be torn down between the moment the
/// invocation list is copied and the moment its handler runs — so an
/// <c>ObjectDisposedException</c> from one page is an ordinary event, not a reason
/// for the sync that published the change to fail. Each is invoked in isolation and
/// its failure logged.
/// </remarks>
public sealed class LibraryChangeNotifier(ILogger<LibraryChangeNotifier> logger) : ILibraryChangeNotifier
{
    private readonly Lock _gate = new();

    private Action<LibraryChange?>? _changed;

    public event Action<LibraryChange?>? Changed
    {
        // Explicit accessors rather than a field-like event: subscription happens on
        // circuit threads and publication on a background one, and the default
        // implementation's combine is not something to rely on for a list that is
        // also being read concurrently.
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public void Publish(LibraryChange? change = null)
    {
        Delegate[] handlers;

        lock (_gate)
        {
            handlers = _changed?.GetInvocationList() ?? [];
        }

        foreach (var handler in handlers.Cast<Action<LibraryChange?>>())
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A page failed to accept a library change notification");
            }
        }
    }
}

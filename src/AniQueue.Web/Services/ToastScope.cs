namespace AniQueue.Web.Services;

/// <summary>
/// Drives <c>Toast</c> for one page: a short confirmation that appears, is read,
/// and takes itself away.
/// </summary>
/// <remarks>
/// The same shape as <see cref="BusyScope"/> — state the page owns, a presentational
/// component that renders it — because a second idiom for "component with timing
/// rules" would be one more thing to learn for no gain.
///
/// <b>Confirmations only.</b> Something that went wrong stays on the page as a notice
/// until it is dealt with; a toast is for the save that worked, where the alternative
/// is silence and silence is indistinguishable from a control that does nothing. That
/// is the whole reason this exists: several settings on the Sources page save the
/// moment they are changed, and until now said so in no way whatsoever.
/// </remarks>
public sealed class ToastScope(Func<Task> notifyStateChanged) : IDisposable
{
    private readonly Func<Task> _notify = notifyStateChanged;

    private CancellationTokenSource? _dismissal;

    /// <summary>
    /// How long a confirmation stays.
    /// </summary>
    /// <remarks>
    /// Long enough to notice and read three words, short enough not to sit over the
    /// page while somebody carries on working. It is not dismissible by hand on
    /// purpose: a close button on a message that closes itself is a control whose only
    /// function is to be in the way.
    /// </remarks>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(3);

    public string? Message { get; private set; }

    public bool IsVisible => Message is not null;

    /// <summary>Shows a confirmation, replacing any still on screen.</summary>
    /// <remarks>
    /// Replacing rather than queuing, because these are confirmations of things the
    /// user just did: the most recent one is the one they are waiting to see, and a
    /// queue would show them an older message first and delay the current one behind
    /// it.
    /// </remarks>
    public async Task ShowAsync(string message)
    {
        // Cancels the dismissal already scheduled, so a second save restarts the clock
        // rather than inheriting whatever was left of the first one's.
        if (_dismissal is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        Message = message;

        var dismissal = new CancellationTokenSource();
        _dismissal = dismissal;

        await _notify();

        // Deliberately not awaited. The caller has finished its work and should not be
        // held for the length of a message it has already displayed.
        _ = DismissAsync(dismissal.Token);
    }

    private async Task DismissAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Duration, cancellationToken);

            Message = null;

            await _notify();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer confirmation, or the page has gone. Either way the
            // message this was going to remove is no longer the one on screen.
        }
        catch (ObjectDisposedException)
        {
            // The circuit went away while the message was still up, which is what
            // happens when somebody saves and immediately navigates. Nothing to
            // announce to a page that is not there.
        }
    }

    public void Dispose()
    {
        _dismissal?.Cancel();
        _dismissal?.Dispose();
        _dismissal = null;
    }
}

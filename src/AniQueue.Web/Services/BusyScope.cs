using System.Globalization;
using AniQueue.Core.Progress;

namespace AniQueue.Web.Services;

/// <summary>
/// Drives <c>BusyDialog</c> for one long-running operation, and owns the timing
/// rules so that every such operation in the application behaves the same way.
///
/// Two rules, and they work as a pair:
///
/// <list type="bullet">
/// <item>
/// Nothing is shown for the first <see cref="ShowDelay"/>. Work that finishes
/// quickly never flashes a dialog, so fast operations still feel instant.
/// </item>
/// <item>
/// Once shown, the dialog stays for at least <see cref="MinimumDuration"/>. This
/// is what makes the steps readable, and it stops the dialog flickering when an
/// operation lands just over the show delay.
/// </item>
/// </list>
///
/// Note what is deliberately absent: padding. The dialog is never held open to
/// manufacture reading time for work that has finished. Its content is real
/// progress from the service doing the work, which for anything slow enough to
/// warrant a dialog is plenty to read.
/// </summary>
public sealed class BusyScope(Func<Task> notifyStateChanged)
{
    private readonly Func<Task> _notify = notifyStateChanged;
    /// <summary>
    /// Replaced wholesale rather than appended to, and that is a correctness fix
    /// rather than a style.
    /// </summary>
    /// <remarks>
    /// The work runs on the thread pool (see <see cref="RunAsync{T}"/>) so progress
    /// arrives on a different thread from the one Blazor renders on. A mutable list
    /// read by the renderer while a report appends to it throws "collection was
    /// modified" from inside <c>BusyDialog</c>'s foreach — which is not an error the
    /// page can catch, because it happens while building the render tree. It takes
    /// the circuit down, and whatever was being described is left half-reported.
    ///
    /// Seen for real on a 182-item apply. Copy-on-write means the renderer always
    /// enumerates a list nobody can touch: a reference assignment is atomic, so the
    /// worst case is a render one step out of date, which the next report fixes.
    /// </remarks>
    private IReadOnlyList<string> _completedSteps = [];

    private DateTimeOffset _shownAt;

    /// <summary>How long work may run before a dialog appears at all.</summary>
    public TimeSpan ShowDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the dialog stays once it has appeared.</summary>
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// True while work is in flight, whether or not a dialog is showing. Controls
    /// are disabled from this rather than from <see cref="IsVisible"/>: during the
    /// show delay the work has started but nothing is on screen yet, and a button
    /// that is still clickable in that window invites the double-submit the dialog
    /// exists to prevent.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>True when the dialog is actually on screen.</summary>
    public bool IsVisible { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public double? Fraction { get; private set; }

    public string? CountText { get; private set; }

    /// <summary>Steps already finished, oldest first.</summary>
    public IReadOnlyList<string> CompletedSteps => _completedSteps;

    /// <summary>
    /// Runs <paramref name="work"/>, showing the dialog if it takes long enough to
    /// be worth one. Progress reported through the supplied reporter drives the
    /// dialog's contents.
    /// </summary>
    public async Task<T> RunAsync<T>(string title, Func<IProgress<OperationProgress>, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Reset(title);

        // Throttled because each report becomes a render batch on the circuit;
        // reporting per item would flood it and slow the work being described.
        var reporter = new ThrottledProgress(new CallbackProgress(OnProgressAsync));

        // Deliberately started on the thread pool rather than awaited inline.
        //
        // Microsoft.Data.Sqlite is a synchronous provider: its async methods are
        // wrappers that complete synchronously. Awaiting an import inline therefore
        // occupies the circuit's single thread for the entire operation, returning
        // an already-completed task — no render can be dispatched, so the page
        // freezes and no dialog can ever appear. Measured on a 700-entry import:
        // twelve seconds during which the whole UI, not just this page, was dead.
        //
        // Moving the work off that thread keeps the circuit free to render, which
        // is what makes both the dialog and its progress possible at all.
        var running = Task.Run(() => work(reporter));

        try
        {
            // The show delay is raced here, on the circuit's synchronisation
            // context, rather than on a background timer.
            //
            // This matters. Blazor Server dispatches renders on that context, so a
            // timer thread that flips a flag and calls StateHasChanged only queues
            // a batch — and if the work is still occupying the context, the batch
            // sits there until it finishes, by which point the dialog has already
            // been hidden again and Blazor coalesces show-then-hide into nothing.
            // Awaiting here yields the context, so the render can actually reach
            // the browser.
            if (await Task.WhenAny(running, Task.Delay(ShowDelay)) != running)
            {
                await ShowAsync();
            }

            return await running;
        }
        finally
        {
            await HideAsync();
        }
    }

    private async Task ShowAsync()
    {
        IsVisible = true;
        _shownAt = DateTimeOffset.UtcNow;
        await _notify();

        // Yield so the batch is flushed before the caller resumes awaiting work.
        await Task.Yield();
    }

    private void Reset(string title)
    {
        Title = title;
        Message = "Starting…";
        Fraction = null;
        CountText = null;
        _completedSteps = [];
        IsVisible = false;
        IsRunning = true;
    }

    private async Task OnProgressAsync(OperationProgress progress)
    {
        // A changed message means the previous step is done; keep it on screen.
        if (!string.IsNullOrEmpty(Message)
            && Message != progress.Message
            && Message != "Starting…"
            && !_completedSteps.Contains(Message))
        {
            // A new list each time, never an append to the one being rendered.
            _completedSteps = [.. _completedSteps, Message];
        }

        Message = progress.Message;
        Fraction = progress.Fraction;

        CountText = progress.HasCount
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} of {1:N0}", progress.Current, progress.Total)
            : null;

        if (IsVisible)
        {
            await _notify();
        }
    }

    private async Task HideAsync()
    {
        if (!IsVisible)
        {
            IsRunning = false;
            return;
        }

        // Shown, so honour the minimum. Without this the dialog can appear and
        // vanish within a frame, which reads as a glitch rather than progress.
        var remaining = MinimumDuration - (DateTimeOffset.UtcNow - _shownAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        IsVisible = false;
        IsRunning = false;
        await _notify();
    }

    /// <summary>Adapts an async callback to <see cref="IProgress{T}"/>.</summary>
    private sealed class CallbackProgress(Func<OperationProgress, Task> callback)
        : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => _ = callback(value);
    }
}

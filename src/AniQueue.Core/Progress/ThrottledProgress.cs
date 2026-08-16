using System.Diagnostics;

namespace AniQueue.Core.Progress;

/// <summary>
/// Passes progress on, but no more often than a fixed interval.
///
/// This exists because of how Blazor Server delivers updates: every report
/// becomes a render batch pushed down the SignalR circuit. Reporting once per
/// item on a 750-entry import would send hundreds of batches, and the flood
/// would slow the operation it is describing while making the numbers unreadable.
///
/// Messages that change and the final report always pass through, so the user
/// still sees each stage and an accurate finish.
/// </summary>
public sealed class ThrottledProgress(
    IProgress<OperationProgress> inner,
    TimeSpan? interval = null) : IProgress<OperationProgress>
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(150);
    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private string? _lastMessage;

    public void Report(OperationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            var stageChanged = value.Message != _lastMessage;
            var finished = value.HasCount && value.Current == value.Total;

            if (!stageChanged && !finished && _sinceLastReport.Elapsed < _interval)
            {
                return;
            }

            _lastMessage = value.Message;
            _sinceLastReport.Restart();
        }

        inner.Report(value);
    }
}

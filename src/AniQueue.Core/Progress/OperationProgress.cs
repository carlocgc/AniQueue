namespace AniQueue.Core.Progress;

/// <summary>
/// A single update from a long-running operation, reported as it happens.
///
/// Deliberately generic: imports, syncs, scoring exports and AI ranking runs all
/// report the same shape, so one dialog can present any of them and services never
/// take a dependency on the UI.
/// </summary>
/// <param name="Message">
/// What is happening right now, phrased for a person rather than a log.
/// </param>
/// <param name="Current">Items processed so far, when the work is countable.</param>
/// <param name="Total">Items expected, when known up front.</param>
/// <param name="Continues">
/// Whether this restates the step already running rather than beginning a new one.
/// </param>
/// <remarks>
/// <b>On <paramref name="Continues"/>:</b> a dialog keeps finished steps on screen,
/// and decides one has finished by the message changing. That is right for work made
/// of stages and wrong for a wait that reports the same thing with a different number
/// in it — a run that ticked its elapsed time every second produced a step per second
/// and buried the dialog in its own clock.
///
/// So a report that only restates the current step says so, and the step list stops
/// growing while the message keeps moving. It is false by default because the common
/// case is genuinely a new stage.
/// </remarks>
public sealed record OperationProgress(
    string Message,
    int? Current = null,
    int? Total = null,
    bool Continues = false)
{
    /// <summary>
    /// Completion between 0 and 1, or null when the operation cannot say — which
    /// is normal at the start, before the size of the work is known.
    /// </summary>
    public double? Fraction =>
        Total is > 0 && Current is >= 0 ? Math.Clamp((double)Current.Value / Total.Value, 0, 1) : null;

    /// <summary>True when there is a count worth displaying alongside the message.</summary>
    public bool HasCount => Total is > 0 && Current is not null;
}

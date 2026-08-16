namespace AniQueue.Core.Progress;

/// <summary>
/// A single update from a long-running operation, reported as it happens.
///
/// Deliberately generic: imports, library export/restore, AniList lookups and AI
/// ranking runs all report the same shape, so one dialog can present any of them
/// and services never take a dependency on the UI.
/// </summary>
/// <param name="Message">
/// What is happening right now, phrased for a person rather than a log.
/// </param>
/// <param name="Current">Items processed so far, when the work is countable.</param>
/// <param name="Total">Items expected, when known up front.</param>
public sealed record OperationProgress(string Message, int? Current = null, int? Total = null)
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

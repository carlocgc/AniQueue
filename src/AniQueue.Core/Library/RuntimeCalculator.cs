using System.Globalization;

namespace AniQueue.Core.Library;

/// <summary>
/// Estimates and formats how long something takes to watch.
///
/// A static class rather than an injected service: these are pure functions over
/// their arguments, with no state and no plausible second implementation. An
/// interface here would buy substitutability nobody wants and cost a constructor
/// parameter everywhere.
///
/// The governing rule is that runtime is never invented. If either the episode
/// count or the episode length is unknown the answer is null, and the UI shows
/// nothing rather than a confident guess.
/// </summary>
public static class RuntimeCalculator
{
    /// <summary>
    /// Total minutes to watch a title, or null when it cannot be known.
    /// </summary>
    public static int? Estimate(int? episodeCount, int? episodeDurationMinutes) =>
        episodeCount is > 0 && episodeDurationMinutes is > 0
            ? episodeCount.Value * episodeDurationMinutes.Value
            : null;

    /// <summary>
    /// Sums runtimes, ignoring entries that cannot be estimated.
    /// </summary>
    /// <returns>
    /// The total, and whether anything was skipped — a franchise total built from
    /// half its entries is misleading unless the UI can say so.
    /// </returns>
    public static (int? Minutes, bool IsPartial) Sum(IEnumerable<int?> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);

        var total = 0;
        var known = 0;
        var unknown = 0;

        foreach (var runtime in runtimes)
        {
            if (runtime is > 0)
            {
                total += runtime.Value;
                known++;
            }
            else
            {
                unknown++;
            }
        }

        return known == 0 ? (null, unknown > 0) : (total, unknown > 0);
    }

    /// <summary>
    /// Human-readable duration: <c>1h 45m</c>, <c>4h 48m</c>, <c>22h</c>, <c>45m</c>.
    /// Whole hours omit the minutes rather than printing a redundant "0m".
    /// </summary>
    public static string? Format(int? minutes)
    {
        if (minutes is not > 0)
        {
            return null;
        }

        var hours = minutes.Value / 60;
        var remainder = minutes.Value % 60;

        return (hours, remainder) switch
        {
            (0, _) => string.Format(CultureInfo.CurrentCulture, "{0}m", remainder),
            (_, 0) => string.Format(CultureInfo.CurrentCulture, "{0}h", hours),
            _ => string.Format(CultureInfo.CurrentCulture, "{0}h {1}m", hours, remainder)
        };
    }
}

namespace AniQueue.Core.Domain;

/// <summary>
/// How often an unattended run reads a source, when one runs at all.
///
/// A fixed set rather than a number of minutes, because every value here is a
/// promise about load on somebody else's API. <see cref="Off"/> is the default, so
/// configuring an account does not by itself start anything fetching.
///
/// Stored as an integer; values are a database contract. Append only — which is
/// why the numbers are out of sequence while the members sit in reading order.
/// </summary>
public enum SyncSchedule
{
    /// <summary>Never runs on its own. Run now still works.</summary>
    Off = 0,

    /// <summary>
    /// Every ten minutes. Offered only while developing, so that a change to a
    /// background task can be watched happening. The value works wherever it is
    /// set, including by hand in <c>userconfig.json</c>.
    /// </summary>
    TenMinutes = 5,

    Hourly = 1,

    EverySixHours = 2,

    Daily = 3,

    Weekly = 4
}

/// <summary>Turns a schedule into the interval the runner waits.</summary>
public static class SyncScheduleExtensions
{
    /// <summary>
    /// The gap between runs, or null for <see cref="SyncSchedule.Off"/>, which is
    /// the absence of a schedule rather than a very long one.
    /// </summary>
    public static TimeSpan? ToInterval(this SyncSchedule schedule) => schedule switch
    {
        SyncSchedule.TenMinutes => TimeSpan.FromMinutes(10),
        SyncSchedule.Hourly => TimeSpan.FromHours(1),
        SyncSchedule.EverySixHours => TimeSpan.FromHours(6),
        SyncSchedule.Daily => TimeSpan.FromDays(1),
        SyncSchedule.Weekly => TimeSpan.FromDays(7),
        _ => null
    };
}

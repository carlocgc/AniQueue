namespace AniQueue.Core.Domain;

/// <summary>
/// How often an unattended run reads a source, when one runs at all.
///
/// A fixed set rather than a number of minutes, because every value here is a
/// promise about load on somebody else's API and an arbitrary field invites
/// "every 2 minutes" from a user who has no way to know the measured rate limit
/// is 30 requests a minute. The shortest choice offered is an hour, which is a
/// long way from anything AniList would object to, and a cron-style schedule is
/// deliberately out of the MVP.
///
/// <see cref="Off"/> is the default, so upgrading an installation that already
/// has an account configured does not start it fetching on its own. Scheduled
/// reads are a thing the user turns on, having read what they do.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncSchedule
{
    /// <summary>Never runs on its own. Sync Now still works.</summary>
    Off = 0,

    Hourly = 1,

    EverySixHours = 2,

    Daily = 3,

    Weekly = 4
}

/// <summary>Turns a schedule into the interval the runner waits.</summary>
public static class SyncScheduleExtensions
{
    /// <summary>
    /// The gap between runs, or null for <see cref="SyncSchedule.Off"/> — which is
    /// the absence of a schedule rather than a very long one, and is worth keeping
    /// distinguishable so nothing can accidentally poll it once a century.
    /// </summary>
    public static TimeSpan? ToInterval(this SyncSchedule schedule) => schedule switch
    {
        SyncSchedule.Hourly => TimeSpan.FromHours(1),
        SyncSchedule.EverySixHours => TimeSpan.FromHours(6),
        SyncSchedule.Daily => TimeSpan.FromDays(1),
        SyncSchedule.Weekly => TimeSpan.FromDays(7),
        _ => null
    };
}

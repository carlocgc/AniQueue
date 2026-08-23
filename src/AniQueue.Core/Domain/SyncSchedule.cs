namespace AniQueue.Core.Domain;

/// <summary>
/// How often an unattended run reads a source, when one runs at all.
///
/// A fixed set rather than a number of minutes, because every value here is a
/// promise about load on somebody else's API and an arbitrary field invites
/// "every 2 minutes" from a user who has no way to know the measured rate limit
/// is 30 requests a minute. A cron-style schedule is deliberately out of the MVP.
///
/// <see cref="Off"/> is the default, so upgrading an installation that already
/// has an account configured does not start it fetching on its own. Scheduled
/// reads are a thing the user turns on, having read what they do.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncSchedule
{
    /// <summary>Never runs on its own. Run now still works.</summary>
    Off = 0,

    /// <summary>
    /// Every ten minutes. Offered only while developing.
    /// </summary>
    /// <remarks>
    /// <b>The number is out of sequence and the position is not.</b> Values here are
    /// a database contract and may only be appended, so this is 5; but reading the
    /// list is how somebody decides what to pick, and an interval that sorts after
    /// "once a week" reads as a mistake. Explicit values let the member sit where it
    /// belongs while the number stays where it was written.
    ///
    /// <b>Why it is not offered in a shipped installation.</b> The shortest interval
    /// otherwise on offer is an hour, and that number is a promise: it is a long way
    /// from anything AniList would object to, and the same cadence now governs
    /// scoring, which is minutes of somebody's GPU per run (D40). Ten minutes exists
    /// so that a change to a background task can be watched happening rather than
    /// waited an hour for — which is a developer's problem, not a user's.
    ///
    /// Gating is on the surface rather than here: the value parses and works
    /// wherever it is set, so an operator who writes it into <c>userconfig.json</c>
    /// deliberately gets it. What development decides is whether the page offers it,
    /// which is where the guess-what-this-costs risk actually lives.
    /// </remarks>
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
    /// The gap between runs, or null for <see cref="SyncSchedule.Off"/> — which is
    /// the absence of a schedule rather than a very long one, and is worth keeping
    /// distinguishable so nothing can accidentally poll it once a century.
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

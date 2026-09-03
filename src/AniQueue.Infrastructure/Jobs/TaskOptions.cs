using AniQueue.Core.Domain;

namespace AniQueue.Infrastructure.Jobs;

/// <summary>
/// The <c>Tasks</c> configuration section: one cadence for all background work.
/// </summary>
/// <remarks>
/// One schedule for every task, so there is one place to look when something has not
/// run. It is the clock and not the gate: every task still decides for itself whether
/// it has anything to do, and still returns immediately when it does not. Staleness
/// rules and the relation backfill's thirty-day marker decide what is work; this
/// decides when to look.
/// </remarks>
public class TaskOptions
{
    /// <summary>Configuration section name, e.g. <c>Tasks:Schedule</c>.</summary>
    public const string SectionName = "Tasks";

    /// <summary>
    /// How often background work is asked whether there is anything to do.
    /// </summary>
    /// <remarks>
    /// Off by default. A scheduled run is a thing the user turns on having read what
    /// it does — one of these spends their bandwidth and another spends their
    /// electricity — and an installation upgrading with an account already configured
    /// must not silently start fetching.
    ///
    /// <see cref="SyncSchedule.Off"/> does not mean nothing ever happens: a task still
    /// runs when its data changes underneath it, and when somebody presses the button.
    /// What it stops is the clock.
    /// </remarks>
    public SyncSchedule Schedule { get; set; } = SyncSchedule.Off;

    /// <summary>
    /// Whether the related-titles pass takes part at all.
    /// </summary>
    /// <remarks>
    /// The pass has a row on the tasks page with a button on it, and a row carrying a
    /// button and no switch invites the question of how to stop it.
    ///
    /// It lives here rather than in a section of its own because there is nothing
    /// else to configure about it: a whole <c>Relations</c> section holding one
    /// boolean would be a home built for a single tenant.
    /// </remarks>
    public bool RelationsEnabled { get; set; } = true;

    /// <summary>
    /// Whether cover art is fetched and cached at all.
    /// </summary>
    /// <remarks>
    /// On by default, unlike <see cref="Schedule"/>. The argument for that one being
    /// off is that a scheduled run spends bandwidth or electricity without anybody
    /// asking; this spends about 16 MB once, against a CDN already serving the same
    /// pictures to the same person, and the alternative default is an application
    /// whose backlog ships as a wall of text until somebody finds a switch.
    ///
    /// It has a switch at all for the reason the relation pass gained one: it has a
    /// row with a button on it, and a row carrying a button and no way to stop it
    /// invites the question of how. Turning it off leaves what is already
    /// cached serving — the pages read the table, not this.
    /// </remarks>
    public bool CoverArtEnabled { get; set; } = true;
}

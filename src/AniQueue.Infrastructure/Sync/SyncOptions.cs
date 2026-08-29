using AniQueue.Core.Domain;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Everything sync reads from configuration — the account, the kill switch and the
/// per-source behaviour, all from one file.
///
/// A section-bound view, not the file itself. <c>UserSettings</c> describes what may
/// be written; this describes what sync needs to read, and binding it to the live
/// section is what lets a save reach an options monitor without a restart.
/// </summary>
public class SyncOptions
{
    /// <summary>Configuration section name, e.g. <c>Sync:Enabled</c>.</summary>
    public const string SectionName = "Sync";

    /// <summary>
    /// The kill switch. False refuses every sync, however it was triggered.
    /// </summary>
    /// <remarks>
    /// The case it exists for is the one where the UI cannot be reached — a sync
    /// hammering a rate limit, or writing something the user wants stopped now.
    /// Editing a file and restarting always works.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which source outranks the others when two of them describe one title. Null
    /// where the file says nothing, which callers read as
    /// <see cref="UserSettings.SyncPrimarySource"/>'s default.
    /// </summary>
    /// <remarks>
    /// One key naming the occupant, because the seat is single by definition. An
    /// integer per source could represent two primaries, or none.
    ///
    /// <b>Nullable because of what is already written down.</b> Every
    /// <c>userconfig.json</c> from before the seat had a default holds
    /// <c>"Sync:PrimarySource": ""</c>, and the configuration binder throws on an
    /// empty string for a non-nullable enum — at startup, before anything is serving,
    /// on exactly the installations that have been running longest. The seat still has
    /// no "nobody" value; the type is what tolerates a file that predates the default.
    /// </remarks>
    public AnimeSource? PrimarySource { get; set; }

    public AniListSyncOptions AniList { get; set; } = new();
}

/// <summary>
/// How AniQueue reads AniList: which account, and what a run is allowed to do.
/// </summary>
/// <remarks>
/// <b>Only a fetchable source has these.</b> Scheduling, conflicts, absence and
/// unattended application are all things a <i>run</i> does, and nothing runs on a
/// file source — MyAnimeList is brought by hand. So there is no
/// <c>Sync:MyAnimeList</c> section, and the Sources page has always gated these
/// controls on <c>CanFetch</c>; this is the same rule expressed in the store.
///
/// A second fetchable source would want its own section rather than a dictionary:
/// <see cref="AnimeSource"/> is a closed enum, the file is written one full key path
/// per line, and a nested map is the shape that spelling exists to avoid.
/// </remarks>
public class AniListSyncOptions
{
    /// <summary>
    /// The AniList username whose list is read. Empty means AniList is not
    /// configured, which the Sources page says plainly rather than failing at fetch
    /// time.
    /// </summary>
    /// <remarks>
    /// Not a credential: AniList serves public lists unauthenticated, verified
    /// against the live API, which is what keeps OAuth out of the MVP.
    /// </remarks>
    public string? UserName { get; set; }

    /// <summary>Whether this source participates in sync at all.</summary>
    /// <remarks>
    /// The ordinary "not right now", as distinct from <see cref="SyncOptions.Enabled"/>,
    /// which stops every source at once. Defaults to enabled, because the settings
    /// that exist for a source are the ones the user went and configured.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether unattended runs commit their unambiguous changes, or hold everything
    /// for review. Defaults to committing, because the safe subset is exactly
    /// what an unattended run can decide without a user.
    /// </summary>
    public bool ApplyUnattended { get; set; } = true;

    /// <summary>What an unattended run does with a conflict.</summary>
    public SyncConflictPolicy ConflictPolicy { get; set; } = SyncConflictPolicy.HoldForReview;

    /// <summary>What happens when this source stops listing a title it once listed.</summary>
    public SyncAbsencePolicy AbsencePolicy { get; set; } = SyncAbsencePolicy.Flag;

    // What a source carries is what a run may do. When a run happens is the shared
    // task cadence, not a per-source setting.
}

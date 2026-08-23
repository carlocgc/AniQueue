using AniQueue.Core.Domain;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Everything sync reads from configuration (D36).
///
/// <b>This is the whole of it now.</b> Until Phase 10a the account and the kill
/// switch lived here while the per-source behaviour lived on a
/// <c>SourceSyncSettings</c> row, so one card on the Sources page wrote to two
/// stores and said nothing about which — the confusion D36 exists to end. D36's
/// table always listed these keys on the file's side; this is that move.
///
/// <b>Still a section-bound view, not the file.</b> <c>UserSettings</c> describes
/// what may be written; this describes what sync needs to read, and binding it to
/// the live section is what lets a save reach an options monitor without a restart.
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
    ///
    /// D40 deletes this in favour of the per-source switch below, on the grounds
    /// that both are now in the same file and reachable the same way, so a global
    /// switch over a single per-source one is one more thing to check. It survives
    /// Phase 10a because Phase 10a is a move and not a redesign.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which source outranks the others when two of them describe one title
    /// (D18, D29, D30). Null until somebody chooses.
    /// </summary>
    /// <remarks>
    /// <b>One key, where there used to be a rank per row.</b> The seat is single by
    /// definition, so naming the occupant expresses that directly — where an integer
    /// per source could represent two primaries, or none, and needed a transaction
    /// across both rows to stop it. That transaction, the demotion write that existed
    /// only so an absent row would not read as a tie, and the arithmetic around
    /// <c>PrimaryRank + 1</c> all go with it.
    ///
    /// <b>Null is honest rather than a gap.</b> With no choice made, two sources tie
    /// and the last import wins, exactly as D29 describes — so the page says nothing
    /// is primary rather than defaulting one of them into the seat.
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
/// per line (D20), and a nested map is the shape that spelling exists to avoid.
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
    /// against the live API, which is what keeps OAuth out of the MVP (D13).
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
    /// for review (D21). Defaults to committing, because the safe subset is exactly
    /// what an unattended run can decide without a user.
    /// </summary>
    public bool ApplyUnattended { get; set; } = true;

    /// <summary>What an unattended run does with a conflict (D21).</summary>
    public SyncConflictPolicy ConflictPolicy { get; set; } = SyncConflictPolicy.HoldForReview;

    /// <summary>What happens when this source stops listing a title it once listed (D19).</summary>
    public SyncAbsencePolicy AbsencePolicy { get; set; } = SyncAbsencePolicy.Flag;

    /// <summary>
    /// How often an unattended run reads this source. Defaults to
    /// <see cref="SyncSchedule.Off"/>.
    /// </summary>
    /// <remarks>
    /// Off by default rather than on, and that is a deliberate cost: an installation
    /// upgrading with an account already configured does not silently start fetching.
    /// Turning it on is the act that carries the intent.
    ///
    /// D40 replaces this with a single cadence covering every background task, and
    /// Phase 15b is where that lands. It is moved here rather than skipped because
    /// deleting it now would leave unattended sync with no schedule at all until the
    /// tasks page exists to supply one.
    /// </remarks>
    public SyncSchedule Schedule { get; set; } = SyncSchedule.Off;
}

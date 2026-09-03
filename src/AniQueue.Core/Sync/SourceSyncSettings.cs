using AniQueue.Core.Domain;

namespace AniQueue.Core.Sync;

/// <summary>
/// How AniQueue treats one external source. Read from <c>userconfig.json</c> rather
/// than from a table.
/// </summary>
/// <remarks>
/// A value rather than a record of what somebody saved. An unset key means the
/// default, so every source always has a complete answer and nothing has to
/// distinguish an absent row from a deliberate one.
///
/// Precedence is not here: primary is one key naming the occupant of a single seat,
/// so <see cref="SourceSyncStatus.IsPrimary"/> is the whole of it.
///
/// Only a fetchable source uses any of this. Every field describes something
/// a <i>run</i> does, and nothing runs on a file source. MyAnimeList carries the
/// defaults and none of them are consulted.
/// </remarks>
public sealed record SourceSyncSettings
{
    /// <summary>The service being described. Never <see cref="AnimeSource.Manual"/>.</summary>
    public required AnimeSource Source { get; init; }

    /// <summary>Whether this source participates in sync at all.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Whether unattended runs commit their unambiguous changes, or hold everything
    /// for review.
    /// </summary>
    public bool ApplyUnattended { get; init; } = true;

    /// <summary>What an unattended run does with a conflict.</summary>
    public SyncConflictPolicy ConflictPolicy { get; init; } = SyncConflictPolicy.HoldForReview;

    /// <summary>What happens when this source stops listing a title it once listed.</summary>
    public SyncAbsencePolicy AbsencePolicy { get; init; } = SyncAbsencePolicy.Flag;

    /// <summary>What a source nobody has configured behaves like.</summary>
    public static SourceSyncSettings DefaultsFor(AnimeSource source) => new() { Source = source };
}

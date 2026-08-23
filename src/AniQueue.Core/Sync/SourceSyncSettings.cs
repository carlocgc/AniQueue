using AniQueue.Core.Domain;

namespace AniQueue.Core.Sync;

/// <summary>
/// How AniQueue treats one external source (D18, D19, D21, D36).
///
/// <b>No longer an entity.</b> Until Phase 10a this was a table keyed
/// <c>(ProfileId, Source)</c>; D36's table always placed these values in
/// <c>userconfig.json</c>, and D40 depends on the move because the task toggles are
/// written there. What is deleted is the row, the configuration and the key — the
/// name survives because it still describes exactly the same thing, read from a
/// different place.
/// </summary>
/// <remarks>
/// <b>A value rather than a record of what somebody saved.</b> There is no "no
/// settings yet" state any more: an unset key means the default, so every source
/// always has a complete answer and nothing has to distinguish an absent row from a
/// deliberate one. The defaults live on the options class that defines them, per
/// D36's rule that a default is not a layer.
///
/// <b>Precedence is not here.</b> It used to be an integer per source, which could
/// express two primaries or none; it is now one key naming the occupant of a single
/// seat, so <see cref="SourceSyncStatus.IsPrimary"/> is the whole of it.
///
/// <b>Only a fetchable source uses any of this.</b> Every field describes something
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
    /// for review (D21).
    /// </summary>
    public bool ApplyUnattended { get; init; } = true;

    /// <summary>What an unattended run does with a conflict (D21).</summary>
    public SyncConflictPolicy ConflictPolicy { get; init; } = SyncConflictPolicy.HoldForReview;

    /// <summary>What happens when this source stops listing a title it once listed (D19).</summary>
    public SyncAbsencePolicy AbsencePolicy { get; init; } = SyncAbsencePolicy.Flag;

    // Schedule was here until Phase 15c. One cadence covers every background task
    // now (D40), so what a source carries is what a run may do rather than when one
    // happens.

    /// <summary>What a source nobody has configured behaves like.</summary>
    public static SourceSyncSettings DefaultsFor(AnimeSource source) => new() { Source = source };
}

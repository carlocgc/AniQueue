namespace AniQueue.Core.Domain;

/// <summary>
/// How one profile treats one external source (D18, D19, D20, D21).
///
/// Keyed <c>(ProfileId, Source)</c>, which is why these are not on
/// <see cref="ProfileSettings"/> — D7's argument for typed columns still holds,
/// but the key is different, and a per-source setting on a per-profile entity
/// would mean a column per source.
///
/// The account identifier is deliberately absent: that is operator configuration
/// and lives in <c>IConfiguration</c>, so the escape hatch when the UI is
/// unreachable is a file rather than a database row (D20).
/// </summary>
public class SourceSyncSettings
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public Profile? Profile { get; set; }

    /// <summary>The service being configured. Never <see cref="AnimeSource.Manual"/>.</summary>
    public AnimeSource Source { get; set; }

    /// <summary>
    /// Who owns tracking data when more than one source knows a title (D18).
    /// **Lower wins.** Rank 0 is the primary source.
    /// </summary>
    /// <remarks>
    /// Consulted only where two sources both claim a title, which makes it inert
    /// for the single-tracker setup D13 optimises for. A lower-ranked source may
    /// still create rows and fill catalogue metadata — precedence guards status,
    /// progress and score, not facts about the title.
    ///
    /// Ranking is explicit rather than inferred. "Whichever source syncs wins" is
    /// the obvious rule and it is wrong for someone migrating away from a service
    /// while still treating their older list as authoritative.
    /// </remarks>
    public int PrecedenceRank { get; set; }

    /// <summary>Whether this source participates in sync at all.</summary>
    public bool IsEnabled { get; set; }

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

    // Deliberately absent until the phase that reads them: the poll interval, whose
    // floor is operator configuration (D20), and the sync watermark, which is
    // bookkeeping for the runner rather than a user preference. Adding columns
    // ahead of their behaviour is what D11 argues against.
}

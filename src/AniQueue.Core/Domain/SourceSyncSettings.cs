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
    /// <remarks>
    /// Defaults to enabled, because the settings that exist for a source are the
    /// ones the user went and configured. The switch that matters for stopping a
    /// sync nobody can reach the UI to stop is the operator's, in configuration
    /// (D20); this one is the ordinary "not right now".
    /// </remarks>
    public bool IsEnabled { get; set; } = true;

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
    /// Off by default rather than on, and that is a deliberate cost: the phase
    /// that added scheduled reads ships them switched off, so an installation
    /// upgrading with an account already configured does not silently start
    /// fetching. Turning it on is the act that carries the intent.
    ///
    /// A user preference rather than operator configuration, because it is a
    /// choice about how closely their library tracks their list — not about the
    /// deployment. The operator's control over unattended runs is the kill switch,
    /// which stops every path into sync regardless of what this says (D20).
    /// </remarks>
    public SyncSchedule Schedule { get; set; } = SyncSchedule.Off;

    // Deliberately still absent: the sync watermark, which is bookkeeping for the
    // runner rather than a user preference — and is not needed, because the runner
    // reads when it last ran from SyncRun, which has to be written anyway.
}

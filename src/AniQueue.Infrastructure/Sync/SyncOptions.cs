namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// The operator's half of sync configuration (D20).
///
/// These are the values a self-hoster needs to reach from outside the application:
/// which account to read, and how to make it stop. They live in
/// <c>IConfiguration</c> — <c>appsettings.json</c>, environment variables, or an
/// optional <c>userconfig.json</c> beside the database — while the user's
/// preferences live in the database on <c>SourceSyncSettings</c>.
///
/// The two key sets are deliberately disjoint, so a value changed in the UI can
/// never be silently reverted by a file, and the escape hatch that matters when the
/// UI is unreachable is a configuration key by design.
/// </summary>
public class SyncOptions
{
    /// <summary>Configuration section name, e.g. <c>Sync:Enabled</c>.</summary>
    public const string SectionName = "Sync";

    /// <summary>
    /// The kill switch. False refuses every sync, however it was triggered.
    /// </summary>
    /// <remarks>
    /// A configuration key rather than a database setting precisely because the
    /// case it exists for is the one where the UI cannot be reached — a sync
    /// hammering a rate limit, or writing something the user wants stopped now.
    /// Editing a file and restarting always works; a toggle inside the application
    /// does not.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    public AniListAccountOptions AniList { get; set; } = new();
}

/// <summary>Which AniList account to read. Not a credential — the list is public.</summary>
public class AniListAccountOptions
{
    /// <summary>
    /// The AniList username whose list is read. Empty means AniList is not
    /// configured, which the Sources page says plainly rather than failing at fetch
    /// time.
    /// </summary>
    /// <remarks>
    /// The account is operator configuration rather than a user preference because
    /// it identifies the deployment's data source. There is no password: AniList
    /// serves public lists unauthenticated, verified against the live API, which is
    /// what keeps OAuth out of the MVP (D13).
    /// </remarks>
    public string? UserName { get; set; }
}

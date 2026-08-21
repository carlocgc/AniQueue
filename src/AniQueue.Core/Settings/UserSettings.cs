namespace AniQueue.Core.Settings;

/// <summary>
/// Everything <c>userconfig.json</c> is allowed to hold, as one typed value.
///
/// <b>Flat on purpose.</b> The file is written one full key path per line —
/// <c>"Sync:AniList:UserName"</c> rather than a nested object — because
/// uncommenting a line out of a nested block leaves its closing braces behind, and
/// a settings file that will not parse is one whose settings are all silently
/// absent (D20). A flat record is the shape that maps one property to one line, so
/// nothing has to reconcile a tree with a list.
/// </summary>
/// <remarks>
/// <b>This is the file, not the options a service consumes.</b> Those remain
/// section-bound views: <c>SyncOptions</c> and <c>ScoringOptions</c> describe what
/// one part of the application needs, while this describes what the operator may
/// write. They overlap without being the same concept, and a service wanting a
/// live value still reads its own options monitor so that a reload reaches it.
/// Editing goes through here; reading does not.
///
/// <b>Defaults live here and nowhere else.</b> D36 removed user-facing keys from
/// <c>appsettings.json</c> entirely: a default is what a key means when unset, not
/// a layer that sets it. The generator writes an unset key as a commented line, so
/// a default changed in a later version reaches an existing installation instead of
/// being pinned by a file written years earlier.
/// </remarks>
public sealed record UserSettings
{
    /// <summary>What a fresh installation behaves like before anything is set.</summary>
    public static UserSettings Defaults { get; } = new();

    /// <summary>
    /// The kill switch. False refuses every sync, however it was triggered.
    /// </summary>
    /// <remarks>
    /// Editable from a page and from the file both, unlike D20's original position.
    /// The property that mattered survives: the file is reachable when the pages
    /// are not, which is the moment this setting exists for.
    /// </remarks>
    public bool SyncEnabled { get; init; } = true;

    /// <summary>
    /// The AniList username whose list is read. Empty means AniList is not
    /// configured, which the Sources page says plainly rather than failing at fetch
    /// time.
    /// </summary>
    /// <remarks>
    /// Not a credential: AniList serves public lists unauthenticated, which is what
    /// keeps OAuth out of the MVP (D13).
    /// </remarks>
    public string? AniListUserName { get; init; }

    /// <summary>The most scored titles a scoring request carries as history.</summary>
    /// <remarks>
    /// Moved off <c>ProfileSettings</c> by D36: it describes somebody else's model
    /// — how much history fits its context — rather than how a page looks, and it
    /// is the file's side of that line.
    /// </remarks>
    public int ScoringHistorySize { get; init; } = 200;

    /// <summary>The most titles to offer for ranking at once, or null for all.</summary>
    /// <remarks>
    /// Null rather than zero for "no limit": zero would mean a request with nothing
    /// in it to rank, and the two must not share a value.
    /// </remarks>
    public int? ScoringCandidateLimit { get; init; }

    /// <summary>How many rankings to ask for back, or null for one per title sent.</summary>
    public int? ScoringReturnTop { get; init; }

    /// <summary>
    /// Whether personal notes travel with a scoring request. Opt in, always.
    /// </summary>
    /// <remarks>
    /// Free text that may contain anything, so §6 excludes it from AI export unless
    /// the user has explicitly asked for it. It moves to the file with the rest of
    /// the scoring settings because it describes what leaves the machine, which is
    /// a property of the integration rather than of a page.
    /// </remarks>
    public bool ScoringIncludePersonalNotes { get; init; }

    /// <summary>
    /// How long to wait for another writer before giving up on a locked database.
    /// </summary>
    /// <remarks>
    /// In the file but on no page, which is a state the design has to allow: it is
    /// worth raising only if large imports report timeouts, and a control for it
    /// would be a knob nobody can evaluate. A key with no editor is fine; an editor
    /// with no key would not be.
    /// </remarks>
    public int DatabaseBusyTimeoutSeconds { get; init; } = 30;
}

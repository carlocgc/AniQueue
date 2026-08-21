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

    /// <summary>Where a self-hosted model is listening, as an origin. Null for none.</summary>
    /// <remarks>
    /// The clearest case of D36's rule: it describes somebody else's software, on the
    /// operator's own network, and AniQueue has no way to discover it. Guarded before
    /// it is used (D38) rather than kept out of reach, because keeping it out of reach
    /// meant editing a file and restarting to change a hostname.
    /// </remarks>
    public string? ScoringEndpoint { get; init; }

    /// <summary>Which model to ask for at that endpoint.</summary>
    public string? ScoringModel { get; init; }

    /// <summary>How long to wait for a ranking, in seconds.</summary>
    public int ScoringTimeoutSeconds { get; init; } = 600;

    /// <summary>Whether to ask the server to constrain its output to JSON.</summary>
    public bool ScoringUseStructuredOutput { get; init; } = true;

    // Scoring:IncludePersonalNotes is not offered, because nothing can write a
    // personal note yet. LibraryEntry.PersonalNotes has a column, §6 protects it,
    // and the import pipeline is careful not to overwrite it — but no surface fills
    // it and no phase has ever built one. A control over whether an always-empty
    // field is exported is not merely useless; it is misleading, because it tells a
    // reader AniQueue has notes and sends them looking for where to write one.
    //
    // The plumbing stays: ScoringOptions binds the key, ScoringRequestOptions carries
    // it, the export gate honours it, and a test holds §6's default of excluded. So
    // an operator who wants it can still set Scoring__IncludePersonalNotes, and the
    // line returns to this file the day notes can be written.

    // The whole Database section is deliberately absent, and for two different
    // reasons that reach the same place.
    //
    // Database:Path cannot be here: this file is found by looking beside the
    // database, so a path set inside it could not be read until it was already in
    // use (D20).
    //
    // Database:BusyTimeoutSeconds could be, and should not. It is not a setting
    // about the user's world — it is how long SQLite waits for a write lock, which
    // is an implementation detail of a storage engine they did not choose. The
    // journal is WAL, so readers never block writers and only two writers can
    // contend at all; thirty seconds is already far past the point where a longer
    // wait is the answer. Anyone waiting that long has a defect, and doubling the
    // number makes the hang twice as long rather than fixing it.
    //
    // Both stay reachable through the environment for the deployment that proves us
    // wrong. What they are not is a line in the file somebody opens when something
    // is already broken, where every line should be one they can act on.
}

using AniQueue.Core.Domain;

namespace AniQueue.Core.Settings;

/// <summary>
/// Everything <c>userconfig.json</c> is allowed to hold, as one typed value.
///
/// <b>Flat on purpose.</b> The file is written one full key path per line —
/// <c>"Sync:AniList:UserName"</c> rather than a nested object — because
/// uncommenting a line out of a nested block leaves its closing braces behind, and
/// a settings file that will not parse is one whose settings are all silently
/// absent. A flat record is the shape that maps one property to one line, so
/// nothing has to reconcile a tree with a list.
/// </summary>
/// <remarks>
/// This is the file, not the options a service consumes. <c>SyncOptions</c> and
/// <c>ScoringOptions</c> stay section-bound views describing what one part of the
/// application needs; a service wanting a live value reads its own options monitor
/// so that a reload reaches it. Editing goes through here; reading does not.
///
/// Defaults live here and nowhere else. <c>appsettings.json</c> carries no
/// user-facing keys, because a default is what a key means when unset rather than a
/// layer that sets it.
/// </remarks>
public sealed record UserSettings
{
    /// <summary>What a fresh installation behaves like before anything is set.</summary>
    public static UserSettings Defaults { get; } = new();

    /// <summary>
    /// The kill switch. False refuses every sync, however it was triggered.
    /// </summary>
    /// <remarks>
    /// Editable from a page and from the file both. The file is reachable when the
    /// pages are not, which is the moment this setting exists for.
    /// </remarks>
    public bool SyncEnabled { get; init; } = true;

    /// <summary>
    /// The AniList username whose list is read. Empty means AniList is not
    /// configured, which the Sources page says plainly rather than failing at fetch
    /// time.
    /// </summary>
    /// <remarks>
    /// Not a credential: AniList serves public lists unauthenticated, which is what
    /// keeps OAuth out of the MVP.
    /// </remarks>
    public string? AniListUserName { get; init; }

    /// <summary>
    /// Which source outranks the others when two of them describe one title.
    /// </summary>
    /// <remarks>
    /// AniList by default, and never empty. An unoccupied seat means two sources tie
    /// and the last import wins, which is what the setting exists to end — so the
    /// control that sets it offers no way back to that state.
    /// </remarks>
    public AnimeSource SyncPrimarySource { get; init; } = AnimeSource.AniList;

    /// <summary>Whether AniList takes part in sync at all.</summary>
    /// <remarks>
    /// The ordinary "not right now", as distinct from <see cref="SyncEnabled"/>,
    /// which stops every source at once.
    /// </remarks>
    public bool AniListEnabled { get; init; } = true;

    /// <summary>
    /// How often every background task is asked whether it has anything to do.
    /// </summary>
    /// <remarks>
    /// One cadence for all of them. Each task still decides for itself whether it is
    /// due and what it has to do; when it is asked is one setting in one place.
    ///
    /// Off by default, deliberately: an installation upgrading with an account
    /// already configured does not
    /// silently start fetching. Turning it on is the act that carries the intent.
    /// </remarks>
    public SyncSchedule TasksSchedule { get; init; } = SyncSchedule.Off;

    /// <summary>Whether the related-titles pass takes part at all.</summary>
    public bool RelationsEnabled { get; init; } = true;

    /// <summary>
    /// Whether an unattended AniList run commits its unambiguous changes, or holds
    /// everything for review.
    /// </summary>
    public bool AniListApplyUnattended { get; init; } = true;

    /// <summary>What an unattended AniList run does with a conflict.</summary>
    public SyncConflictPolicy AniListConflictPolicy { get; init; } = SyncConflictPolicy.HoldForReview;

    /// <summary>What happens when AniList stops listing a title it once listed.</summary>
    public SyncAbsencePolicy AniListAbsencePolicy { get; init; } = SyncAbsencePolicy.Flag;

    // There is no MyAnimeList section. Every setting above describes something a run
    // does, and nothing runs on a file source — the Sources page has always gated
    // these controls on CanFetch, and this is the same rule expressed in the store.

    /// <summary>
    /// The most scored titles a request carries as history, or null for all. How much
    /// history fits is a property of somebody else's model, which is why it is here
    /// rather than in the database.
    /// </summary>
    public int? ScoringHistorySize { get; init; } = 200;

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
    /// Guarded before it is used rather than kept out of reach, because keeping it
    /// out of reach would mean editing a file and restarting to change a hostname.
    /// </remarks>
    public string? ScoringEndpoint { get; init; }

    /// <summary>Which model to ask for at that endpoint.</summary>
    public string? ScoringModel { get; init; }

    /// <summary>How long to wait for a ranking, in seconds.</summary>
    public int ScoringTimeoutSeconds { get; init; } = 600;

    /// <summary>Whether to ask the server to constrain its output to JSON.</summary>
    public bool ScoringUseStructuredOutput { get; init; } = true;

    /// <summary>How many further titles must be rated before a score is stale.</summary>
    public int ScoringStaleAfterRatings { get; init; } = 5;

    /// <summary>Whether a scheduled sweep may ask a remote model. Off until asked for.</summary>
    /// <remarks>
    /// The only switch the remote route has. It gates <c>ScoringSweepJob</c> and
    /// nothing else; the manual paste route never consults it.
    ///
    /// Off by default because whether the route works at all depends on the model,
    /// and AniQueue cannot tell in advance: some models spend their entire output
    /// budget reasoning and never produce JSON. Nothing should spend somebody's
    /// electricity before they have asked for it.
    /// </remarks>
    public bool ScoringEnabled { get; init; }


    /// <summary>How many titles one unattended batch carries.</summary>
    /// <remarks>
    /// A batch is a generation length, and small models degrade over long ones: a
    /// larger batch mostly produces replies that stop early or run out of output
    /// budget. A short reply is a valid ranking, so the titles left out simply stay
    /// unscored and are picked again next batch.
    ///
    /// Small batches are affordable because the request does not break the server's
    /// prompt cache, so the history is processed once per sweep rather than once per
    /// batch. See <c>ScoringRequestWriter</c>.
    /// </remarks>
    public int ScoringBatchSize { get; init; } = 10;

    /// <summary>How long one sweep may keep going, in minutes.</summary>
    public int ScoringSweepMinutes { get; init; } = 60;

    /// <summary>
    /// Clears the login password on the next start. The way back in when it has
    /// been forgotten.
    /// </summary>
    /// <remarks>
    /// The start that acts on it writes this back to false, so the escape hatch
    /// cannot quietly wipe the new password on the restart after. It is here rather
    /// than beside the password itself because the file is what somebody can reach
    /// when the pages are the thing locking them out.
    /// </remarks>
    public bool AuthClearPassword { get; init; }

    // Scoring:IncludePersonalNotes is not written to the file, because no surface
    // fills LibraryEntry.PersonalNotes yet and a control over an always-empty field
    // would send a reader looking for where to write one. The key still binds, so an
    // operator can set Scoring__IncludePersonalNotes; the default stays excluded.

    // The Database section is deliberately absent. Database:Path could not live here
    // even in principle — this file is found by looking beside the database — and
    // Database:BusyTimeoutSeconds is a storage-engine detail rather than a setting
    // about the user's library. Both stay reachable through the environment.
}

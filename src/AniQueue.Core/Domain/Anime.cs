namespace AniQueue.Core.Domain;

/// <summary>
/// A single anime title — the catalogue record, independent of any user's opinion
/// of it. What the user thinks and whether they have watched it lives on
/// <see cref="LibraryEntry"/>.
///
/// This type is never coupled to a MyAnimeList or AniList DTO. Importers map into
/// it; it does not mirror any external schema.
///
/// Nothing here says which group a title belongs to, and nothing will (D24).
/// Titles are related to one another rather than gathered into groups: what a
/// title's relatives are is a fact AniList publishes about that title, so it is
/// stored as edges elsewhere rather than as membership of a set AniQueue invented
/// and a user had to maintain.
/// </summary>
public class Anime
{
    public int Id { get; set; }

    /// <summary>
    /// The title as displayed: resolved from the variants below through the
    /// profile's preferred language, and the only title anything else reads.
    /// </summary>
    /// <remarks>
    /// Denormalised deliberately. The backlog searches, sorts and pages on this
    /// column in SQL, the queue and the AI export read it, and pushing a
    /// per-profile language choice into every one of those queries would buy
    /// nothing: recomputing this column when the preference changes is one
    /// statement, and it happens about as often as someone changes their theme.
    ///
    /// For a manual entry or a MyAnimeList import this is the only title there is
    /// — those sources publish one name — and the variants below stay null.
    /// </remarks>
    public required string Title { get; set; }

    /// <summary>
    /// The romanised title, where the source published one.
    /// </summary>
    /// <remarks>
    /// Three typed columns rather than one <c>AlternativeTitle</c>, which is what
    /// this replaced. That column held whichever variant happened to differ, with
    /// nothing recording which language it was, so nothing could ever switch
    /// between them without guessing — changing the displayed language meant
    /// re-fetching the entire list from the source (D22).
    ///
    /// Typed columns rather than a title-per-row table for the reason D7 gives
    /// about settings: the set is fixed and known — <see cref="TitleLanguage"/>
    /// has three members and no plan for a fourth — so columns stay migratable and
    /// keep the search above trivial, where a key/value bag would be stringly
    /// typed and would drag a join into every query.
    /// </remarks>
    public string? TitleRomaji { get; set; }

    /// <summary>The official English title. Absent for roughly one title in seven.</summary>
    public string? TitleEnglish { get; set; }

    /// <summary>The title in its original script.</summary>
    public string? TitleNative { get; set; }

    public MediaType MediaType { get; set; } = MediaType.Unknown;

    /// <summary>Total episodes, where known. Null for an unknown or ongoing count.</summary>
    public int? EpisodeCount { get; set; }

    /// <summary>
    /// Typical episode length. Null when unknown — runtime is then not estimated
    /// at all rather than guessed (ROADMAP.md §7, Phase 5).
    /// </summary>
    public int? EpisodeDurationMinutes { get; set; }

    public int? ReleaseYear { get; set; }

    /// <summary>
    /// The date the title first aired, where the source published one.
    /// </summary>
    /// <remarks>
    /// A year is not enough to order by (D24). Split-cour seasons share one, and
    /// putting two halves of the same series in an arbitrary order is exactly the
    /// case relation ordering exists to get right. <see cref="ReleaseYear"/> stays
    /// beside it because the decade filter groups on it in SQL and a MyAnimeList
    /// import supplies nothing finer.
    ///
    /// Populated by the relation pass rather than the list sync, which visits every
    /// title anyway and can carry it for no extra request.
    /// </remarks>
    public DateOnly? StartDate { get; set; }

    /// <summary>
    /// Remote URL only. Image binaries are never stored in the database; the
    /// application must render correctly when this is null or unreachable.
    /// </summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// The dominant colour of the cover art, as <c>#rrggbb</c>, where the source
    /// published one.
    /// </summary>
    /// <remarks>
    /// Six bytes, present for 92% of titles, and it gives a themed card with no
    /// image loading at all — which is both a decision aid on its own and the
    /// degradation Phase 9.5 needs for a cover that is missing or still downloading
    /// (D25). Taken here because the relation pass is already asking about every
    /// title; nothing renders it yet.
    /// </remarks>
    public string? CoverImageColor { get; set; }

    public string? Description { get; set; }

    public AnimeSource Source { get; set; } = AnimeSource.Manual;

    /// <summary>
    /// Every external service that identifies this title (D17).
    /// </summary>
    /// <remarks>
    /// Empty for manual entries. More than one is the normal case for anything
    /// AniList knows, since it publishes a MyAnimeList id alongside its own — and
    /// that second identifier is what lets a sync match a MyAnimeList-imported row
    /// rather than duplicate it.
    ///
    /// This replaced a single <c>SourceAnimeId</c> column. One column could hold
    /// one identity, so a library imported from one service and synced from another
    /// matched nothing and conflicted on every title.
    /// </remarks>
    public ICollection<AnimeExternalId> ExternalIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

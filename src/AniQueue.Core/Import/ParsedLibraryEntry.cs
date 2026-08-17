using AniQueue.Core.Domain;

namespace AniQueue.Core.Import;

/// <summary>
/// One entry as read from an import file, normalised into AniQueue's own terms.
///
/// This is the seam that stops MyAnimeList's (or later AniList's) vocabulary from
/// leaking into the domain: parsers translate into this shape, and everything
/// downstream works from it without knowing which format produced it.
/// </summary>
/// <remarks>
/// <b>Equality is not fully structural.</b> A record compares an
/// <see cref="IReadOnlyList{T}"/> member by reference, so two instances with
/// identical <see cref="ExternalIds"/> are unequal. Nothing downstream compares
/// these — matching works from the identifiers themselves — so no custom equality
/// is implemented rather than carrying one nothing needs. Compare serialised form
/// if two parses ever have to be checked against each other.
/// </remarks>
public sealed record ParsedLibraryEntry
{
    /// <summary>
    /// Which format produced this entry. Provenance only — matching uses
    /// <see cref="ExternalIds"/>.
    /// </summary>
    public required AnimeSource Source { get; init; }

    /// <summary>
    /// Every identifier this record supplies, which may be more than one (D17).
    /// </summary>
    /// <remarks>
    /// A MyAnimeList export knows only itself and supplies one, or none for an
    /// entry missing its id. An AniList response supplies its own id and the
    /// MyAnimeList id it publishes alongside, and storing both is what makes the
    /// bridge between the two services work in whichever order the user imports.
    ///
    /// Empty is legitimate and means the entry can only be matched by title.
    /// </remarks>
    public IReadOnlyList<ExternalIdentifier> ExternalIds { get; init; } = [];

    public required string Title { get; init; }

    /// <summary>
    /// The title variant the user did not ask to see (D22). Null for any source
    /// that publishes only one — which is every MyAnimeList export.
    /// </summary>
    public string? AlternativeTitle { get; init; }

    public MediaType MediaType { get; init; } = MediaType.Unknown;

    /// <summary>Null when unknown. Sources routinely write 0 to mean "unknown".</summary>
    public int? EpisodeCount { get; init; }

    /// <summary>
    /// Typical episode length, where the source states one.
    /// </summary>
    /// <remarks>
    /// This and the two fields below are catalogue facts rather than tracking data,
    /// so D18's precedence never guards them — whichever source has them fills them
    /// in. A MyAnimeList export carries none of the three, which is why every
    /// runtime and decade surface built in Phase 3 sat inert until an AniList sync
    /// existed to populate them.
    /// </remarks>
    public int? EpisodeDurationMinutes { get; init; }

    public int? ReleaseYear { get; init; }

    /// <summary>Remote URL only. Nothing downloads or stores the image.</summary>
    public string? CoverImageUrl { get; init; }

    public LibraryStatus Status { get; init; } = LibraryStatus.Planning;

    public int EpisodesWatched { get; init; }

    /// <summary>1–10, or null when unscored. Never 0 — that is a source convention, not a rating.</summary>
    public int? UserScore { get; init; }

    public DateOnly? DateStarted { get; init; }

    public DateOnly? DateCompleted { get; init; }

    public int TimesRewatched { get; init; }
}

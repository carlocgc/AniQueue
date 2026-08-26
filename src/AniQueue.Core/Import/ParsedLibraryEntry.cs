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

    /// <summary>
    /// The title to fall back on: whatever single name the format publishes.
    /// </summary>
    /// <remarks>
    /// Every source has one of these. Only some publish the variants below, and a
    /// parser never decides which of them to display — that is the profile's
    /// preference, applied where the row is written, so the same parse serves a
    /// user reading romaji and a user reading English.
    /// </remarks>
    public required string Title { get; init; }

    /// <summary>Variants, each against its language, for sources that publish them (D22).</summary>
    public string? TitleRomaji { get; init; }

    public string? TitleEnglish { get; init; }

    public string? TitleNative { get; init; }

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

    /// <summary>
    /// Where the source says the cover is. The parser does not fetch it.
    /// </summary>
    /// <remarks>
    /// This lands on an <c>AnimeImage</c> row rather than a column, and it is the
    /// <i>thumbnail</i> size rather than the largest available (D47). §10 took
    /// <c>extraLarge</c> on the reasoning that nothing rendered art yet, which was
    /// right about the timing and wrong about the size: something renders it now, in
    /// a forty-pixel column, and the same picture is 9.7 KB or 83.3 KB depending only
    /// on which field named it.
    /// </remarks>
    public string? CoverImageUrl { get; init; }

    /// <summary>
    /// Where the source says the full-size cover is, for the detail dialog (D48).
    /// </summary>
    /// <remarks>
    /// A second field rather than a second entry, because both sizes arrive on the
    /// same title in the same response and splitting them would mean the parser
    /// producing two records for one row. They become two <c>AnimeImage</c> rows,
    /// which is where they stop travelling together.
    /// </remarks>
    public string? CoverImageFullUrl { get; init; }

    /// <summary>
    /// The synopsis, as the source published it. Never transformed here (D49).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Genres the source names, or empty when it names none.
    /// </summary>
    /// <remarks>
    /// <b>Empty means the source did not say</b>, and every consumer has to read it
    /// that way. A MyAnimeList export publishes no genres at all, so treating empty
    /// as a statement of fact would strip whatever AniList supplied off every title
    /// the two sources share — the collection form of the rule <c>Merge</c> already
    /// keeps for scalars (D49).
    /// </remarks>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Companies the source credits, or empty when it credits none.</summary>
    /// <remarks>Empty means silence, for <see cref="Genres"/>' reason.</remarks>
    public IReadOnlyList<ParsedStudio> Studios { get; init; } = [];

    public LibraryStatus Status { get; init; } = LibraryStatus.Planning;

    public int EpisodesWatched { get; init; }

    /// <summary>1–10, or null when unscored. Never 0 — that is a source convention, not a rating.</summary>
    public int? UserScore { get; init; }

    public DateOnly? DateStarted { get; init; }

    public DateOnly? DateCompleted { get; init; }

    public int TimesRewatched { get; init; }
}

/// <summary>
/// One company credited on a title, as the source states it (D49).
/// </summary>
/// <param name="Name">The company's name, verbatim.</param>
/// <param name="IsMain">
/// Whether the source marks this as the primary studio. False for producers, and
/// false for every edge on a title where the source flags none.
/// </param>
/// <param name="IsAnimationStudio">
/// Whether the company animates rather than funds. A fact about the company, carried
/// alongside the pairing because it arrives in the same edge.
/// </param>
public readonly record struct ParsedStudio(string Name, bool IsMain, bool IsAnimationStudio);

namespace AniQueue.Core.Domain;

/// <summary>
/// A single anime title — the catalogue record, independent of any user's opinion
/// of it. What the user thinks and whether they have watched it lives on
/// <see cref="LibraryEntry"/>.
/// </summary>
public class Anime
{
    public int Id { get; set; }

    /// <summary>
    /// The title as displayed: resolved from the variants below through the
    /// profile's preferred language, and the only title anything else reads.
    /// Recomputed when the language preference changes.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>The romanised title, where the source published one.</summary>
    public string? TitleRomaji { get; set; }

    /// <summary>The official English title. Absent for roughly one title in seven.</summary>
    public string? TitleEnglish { get; set; }

    /// <summary>The title in its original script.</summary>
    public string? TitleNative { get; set; }

    public MediaType MediaType { get; set; } = MediaType.Unknown;

    /// <summary>Total episodes, where known. Null for an unknown or ongoing count.</summary>
    public int? EpisodeCount { get; set; }

    /// <summary>
    /// Typical episode length. Null when unknown, in which case runtime is not
    /// estimated at all.
    /// </summary>
    public int? EpisodeDurationMinutes { get; set; }

    /// <summary>The year of first airing. What the decade filter groups on.</summary>
    public int? ReleaseYear { get; set; }

    /// <summary>
    /// The date the title first aired, where the source published one. Orders
    /// related titles, which a year alone cannot do for split-cour seasons.
    /// </summary>
    public DateOnly? StartDate { get; set; }

    /// <summary>Every picture of this title, by kind, source and rendition.</summary>
    public ICollection<AnimeImage> Images { get; set; } = [];

    /// <summary>
    /// The dominant colour of the cover art, as <c>#rrggbb</c>, where the source
    /// published one. Lets a card be themed with no image loaded.
    /// </summary>
    public string? CoverImageColor { get; set; }

    /// <summary>
    /// The synopsis, as AniList's own markdown. Kept unrendered so that spoilers
    /// keep their <c>~!...!~</c> delimiters and no third-party HTML reaches the DOM.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Genres AniList publishes for this title. Empty means the source did not
    /// say, never that the title has no genres.
    /// </summary>
    public ICollection<AnimeGenre> Genres { get; set; } = [];

    /// <summary>Studios and producers, with the main one flagged.</summary>
    public ICollection<AnimeStudio> Studios { get; set; } = [];

    /// <summary>How the record came to be here. Identity lives on <see cref="ExternalIds"/>.</summary>
    public AnimeSource Source { get; set; } = AnimeSource.Manual;

    /// <summary>
    /// Every external service that identifies this title. Empty for manual
    /// entries; more than one is normal, since AniList publishes a MyAnimeList id
    /// alongside its own.
    /// </summary>
    public ICollection<AnimeExternalId> ExternalIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

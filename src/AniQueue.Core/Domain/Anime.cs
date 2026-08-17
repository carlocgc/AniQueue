namespace AniQueue.Core.Domain;

/// <summary>
/// A single anime title — the catalogue record, independent of any user's opinion
/// of it. What the user thinks and whether they have watched it lives on
/// <see cref="LibraryEntry"/>.
///
/// This type is never coupled to a MyAnimeList or AniList DTO. Importers map into
/// it; it does not mirror any external schema.
/// </summary>
public class Anime
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public string? AlternativeTitle { get; set; }

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
    /// Remote URL only. Image binaries are never stored in the database; the
    /// application must render correctly when this is null or unreachable.
    /// </summary>
    public string? CoverImageUrl { get; set; }

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

    public int? FranchiseId { get; set; }

    public Franchise? Franchise { get; set; }

    /// <summary>Position in the franchise's viewing order. Null when unsequenced.</summary>
    public int? FranchiseOrder { get; set; }

    /// <summary>
    /// Marks a side entry — a special or bonus OVA — that should not prevent its
    /// franchise from counting as substantially complete.
    ///
    /// This lives on the entry rather than the franchise because it describes an
    /// individual title's role within the group.
    /// </summary>
    public bool OptionalWithinFranchise { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

using AniQueue.Core.Artwork;
using AniQueue.Core.Domain;
using AniQueue.Core.Progress;

namespace AniQueue.Core.Library;

/// <summary>A single row in a backlog or library listing.</summary>
public sealed record LibraryListItem
{
    public required int AnimeId { get; init; }

    public required string Title { get; init; }

    public MediaType MediaType { get; init; }

    public int? EpisodeCount { get; init; }

    public int? EpisodeDurationMinutes { get; init; }

    public int? ReleaseYear { get; init; }

    public required LibraryStatus Status { get; init; }

    public int EpisodesWatched { get; init; }

    public int? UserScore { get; init; }

    public bool IsHidden { get; init; }

    public double? RecommendationScore { get; init; }

    public double? RecommendationConfidence { get; init; }

    /// <summary>How this record came to exist here. Provenance, not identity (D17).</summary>
    public AnimeSource Source { get; init; }

    /// <summary>Every service that identifies this title.</summary>
    public IReadOnlyList<ExternalIdentifier> ExternalIds { get; init; } = [];

    /// <summary>Whether this title already occupies a slot in the Up Next queue.</summary>
    public bool IsQueued { get; init; }

    /// <summary>The hash of the cached poster, or null while there is not one.</summary>
    public string? CoverContentHash { get; init; }

    /// <summary>The cached poster's extension, which travels in its served URL.</summary>
    public string? CoverFileExtension { get; init; }

    /// <summary>The dominant colour of the cover, as the source published it.</summary>
    public string? CoverImageColor { get; init; }

    /// <summary>
    /// What this row should render for art: a served picture, a colour, or nothing.
    /// </summary>
    /// <remarks>
    /// Computed here rather than in markup for the reason §3 gives about components —
    /// no logic in a <c>.razor</c> file — and the three columns above are what the
    /// query carries so that the page needs no second lookup per row. Nothing here
    /// reaches the filesystem: the endpoint answers a miss with a 404 and the job
    /// repairs the row, which keeps I/O out of a render that happens on every paint.
    /// </remarks>
    public CoverArt Cover => CoverImageResolver.ForAnime(
        AnimeId, CoverContentHash, CoverFileExtension, CoverImageColor);

    /// <summary>Estimated minutes to watch, or null when it cannot be known.</summary>
    public int? EstimatedRuntimeMinutes => RuntimeCalculator.Estimate(EpisodeCount, EpisodeDurationMinutes);

    /// <summary>Links out to every site that knows this title, in a stable order.</summary>
    public IReadOnlyList<SourceLink> SourceLinks => SourceLinkBuilder.ForAnime(ExternalIds);
}

/// <summary>
/// Everything the detail dialog argues with (D49).
/// </summary>
/// <remarks>
/// <b>A second query rather than more columns on the list.</b> The backlog carries
/// fifty rows and needs none of this; the dialog shows one title and needs all of it.
/// Loading genres, studios and a synopsis for every row to serve the one a user might
/// open would put four collection joins on the page's hot query to save a lookup that
/// happens at human speed.
///
/// Its purpose is narrower than a detail page's, and that is what decides what is
/// here: this exists to make an unwatched title look worth queueing, so it carries
/// what argues for a show and nothing that merely administers one.
/// </remarks>
public sealed record TitleDetail
{
    public required int AnimeId { get; init; }

    public required string Title { get; init; }

    public MediaType MediaType { get; init; }

    public int? EpisodeCount { get; init; }

    public int? EpisodeDurationMinutes { get; init; }

    public int? ReleaseYear { get; init; }

    public LibraryStatus Status { get; init; }

    public int EpisodesWatched { get; init; }

    public int? UserScore { get; init; }

    public bool IsQueued { get; init; }

    /// <summary>The synopsis as AniList published it, still in its own markdown.</summary>
    /// <remarks>
    /// Not formatted here. <see cref="SynopsisFormatter"/> turns it into runs the page
    /// renders as text, and keeping the two apart is what lets the formatting be
    /// tested exhaustively without a database anywhere near it (D49).
    /// </remarks>
    public string? Synopsis { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>The studio AniList marks as primary, where it marks one.</summary>
    /// <remarks>
    /// Null is common and is rendered as no studio line at all rather than as a
    /// placeholder — D25's silent degradation, which every other field here follows.
    /// </remarks>
    public string? MainStudio { get; init; }

    public double? RecommendationScore { get; init; }

    public double? RecommendationConfidence { get; init; }

    /// <summary>Why the model scored it that way, verbatim and untrusted.</summary>
    public string? RecommendationReason { get; init; }

    public AnimeSource Source { get; init; }

    public IReadOnlyList<ExternalIdentifier> ExternalIds { get; init; } = [];

    /// <summary>The full-size poster's hash, or null while it has not been cached.</summary>
    public string? PosterContentHash { get; init; }

    public string? PosterFileExtension { get; init; }

    /// <summary>The thumbnail's, which is what the dialog falls back to.</summary>
    public string? ThumbnailContentHash { get; init; }

    public string? ThumbnailFileExtension { get; init; }

    public string? CoverImageColor { get; init; }

    /// <summary>
    /// The picture to show, at the best size that has actually arrived.
    /// </summary>
    /// <remarks>
    /// <b>Four steps down, and every one of them is reachable.</b> The full-size
    /// cover, then the thumbnail the list is already showing, then the colour block
    /// Phase 6 banked, then nothing. The second step is the one that matters in
    /// practice: a fresh install has thumbnails for minutes before it has posters
    /// (D48), and a dialog that showed a colour block during that window would look
    /// broken beside a list row showing art for the same title.
    /// </remarks>
    public CoverArt Poster => PosterContentHash is { Length: > 0 }
        ? CoverImageResolver.ForAnime(
            AnimeId, PosterContentHash, PosterFileExtension, CoverImageColor, ImageKind.Poster, ImageRendition.Full)
        : CoverImageResolver.ForAnime(
            AnimeId, ThumbnailContentHash, ThumbnailFileExtension, CoverImageColor);

    public int? EstimatedRuntimeMinutes => RuntimeCalculator.Estimate(EpisodeCount, EpisodeDurationMinutes);

    public IReadOnlyList<SourceLink> SourceLinks => SourceLinkBuilder.ForAnime(ExternalIds);

    /// <summary>The synopsis in runs, masked where AniList marked a spoiler.</summary>
    public IReadOnlyList<SynopsisSegment> SynopsisSegments => SynopsisFormatter.Parse(Synopsis);
}

/// <summary>Counts for the library as a whole.</summary>
public sealed record LibrarySummary
{
    public required int Total { get; init; }

    public required IReadOnlyDictionary<LibraryStatus, int> ByStatus { get; init; }

    public int Of(LibraryStatus status) => ByStatus.GetValueOrDefault(status);
}

/// <summary>A page of results, with the total available for paging controls.</summary>
public sealed record LibraryPage
{
    public required IReadOnlyList<LibraryListItem> Items { get; init; }

    public required int TotalCount { get; init; }
}

/// <summary>What a bulk action did.</summary>
public sealed record BulkActionResult(int Affected, int Skipped);

/// <summary>
/// Reads and bulk-edits the library.
///
/// Filtering, sorting and paging all happen in the database: the application is
/// expected to hold several thousand titles, and doing any of it in memory would
/// defeat the paging above it.
/// </summary>
public interface ILibraryService
{
    Task<LibrarySummary> GetSummaryAsync(int profileId, CancellationToken cancellationToken = default);

    Task<LibraryPage> GetPageAsync(
        int profileId,
        LibraryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the library contains, so the UI can offer only filters that could
    /// match something.
    /// </summary>
    /// <summary>
    /// Everything the detail dialog needs about one title, or null if it has gone.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, because the row a user clicked can be deleted by
    /// a sync between the page rendering and the dialog opening — and a missing title
    /// is a dialog that does not open, not an error worth showing (D25).
    /// </remarks>
    Task<TitleDetail?> GetTitleDetailAsync(
        int profileId,
        int animeId,
        CancellationToken cancellationToken = default);

    Task<LibraryFacets> GetFacetsAsync(int profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hides or unhides many entries. Hiding keeps the entry and its history; it
    /// only removes it from listings, so it is always reversible.
    /// </summary>
    Task<BulkActionResult> SetHiddenAsync(
        int profileId,
        IReadOnlyCollection<int> animeIds,
        bool hidden,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

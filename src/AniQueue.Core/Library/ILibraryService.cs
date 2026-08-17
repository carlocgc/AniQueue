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

    public string? FranchiseName { get; init; }

    public double? RecommendationScore { get; init; }

    public double? RecommendationConfidence { get; init; }

    /// <summary>How this record came to exist here. Provenance, not identity (D17).</summary>
    public AnimeSource Source { get; init; }

    /// <summary>Every service that identifies this title.</summary>
    public IReadOnlyList<ExternalIdentifier> ExternalIds { get; init; } = [];

    /// <summary>Whether this title already occupies a slot in the Up Next queue.</summary>
    public bool IsQueued { get; init; }

    /// <summary>Estimated minutes to watch, or null when it cannot be known.</summary>
    public int? EstimatedRuntimeMinutes => RuntimeCalculator.Estimate(EpisodeCount, EpisodeDurationMinutes);

    /// <summary>Links out to every site that knows this title, in a stable order.</summary>
    public IReadOnlyList<SourceLink> SourceLinks => SourceLinkBuilder.ForAnime(ExternalIds);
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

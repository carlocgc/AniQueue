using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>A single row in a backlog or library listing.</summary>
public sealed record LibraryListItem
{
    public required int AnimeId { get; init; }

    public required string Title { get; init; }

    public MediaType MediaType { get; init; }

    public int? EpisodeCount { get; init; }

    public int? ReleaseYear { get; init; }

    public required LibraryStatus Status { get; init; }

    public int EpisodesWatched { get; init; }

    public int? UserScore { get; init; }

    public string? FranchiseName { get; init; }

    public double? RecommendationScore { get; init; }
}

/// <summary>Counts for the library as a whole.</summary>
public sealed record LibrarySummary
{
    public required int Total { get; init; }

    public required IReadOnlyDictionary<LibraryStatus, int> ByStatus { get; init; }

    public int Of(LibraryStatus status) => ByStatus.GetValueOrDefault(status);
}

/// <summary>How to filter and page a library listing.</summary>
public sealed record LibraryQuery
{
    public LibraryStatus? Status { get; init; }

    /// <summary>Case-insensitive substring match on the title.</summary>
    public string? Search { get; init; }

    /// <summary>Hidden entries stay in the library but drop out of listings.</summary>
    public bool IncludeHidden { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 50;
}

/// <summary>A page of results, with the total available for paging controls.</summary>
public sealed record LibraryPage
{
    public required IReadOnlyList<LibraryListItem> Items { get; init; }

    public required int TotalCount { get; init; }
}

/// <summary>
/// Reads the library for display.
///
/// Phase 2 needs only enough to prove imported data landed; the full filtering,
/// sorting and bulk-action surface arrives with the backlog page in Phase 3.
/// Filtering and paging happen in the database, not in memory — the application
/// is expected to handle libraries of several thousand titles.
/// </summary>
public interface ILibraryService
{
    Task<LibrarySummary> GetSummaryAsync(int profileId, CancellationToken cancellationToken = default);

    Task<LibraryPage> GetPageAsync(
        int profileId,
        LibraryQuery query,
        CancellationToken cancellationToken = default);
}

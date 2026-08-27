using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>How a backlog listing is ordered.</summary>
public enum LibrarySort
{
    TitleAscending = 0,
    TitleDescending = 1,

    /// <summary>Highest AI predicted score first. Unranked entries sort last.</summary>
    RecommendationDescending = 2,

    // 3 was PriorityDescending, removed with ManualPriority (D14). The value is
    // not reused: these are persisted in settings later, and silently changing
    // what a stored number means is how a saved preference becomes a wrong one.

    /// <summary>Shortest first — the "I have an hour" sort. Unknown runtimes sort last.</summary>
    RuntimeAscending = 4,
    RuntimeDescending = 5,

    /// <summary>Newest release first. Unknown years sort last.</summary>
    YearDescending = 6,
    YearAscending = 7,

    /// <summary>Most recently added to the library first.</summary>
    DateAddedDescending = 8,

    /// <summary>The user's own score, highest first.</summary>
    UserScoreDescending = 9
}

/// <summary>
/// How to filter, sort and page a backlog listing.
///
/// Every member is optional and null means "do not filter on this", so a caller
/// only states what it cares about. All of it is applied in the database — the
/// application is expected to hold several thousand titles, and filtering a list
/// that size in memory would defeat the paging above it.
/// </summary>
public sealed record LibraryQuery
{
    /// <summary>
    /// Defaults to Planning. The backlog is what the user intends to watch;
    /// Watching has its own page, and listing every status by default buries the
    /// entries that are actually a decision behind the ones that are not.
    /// Set explicitly to null to widen it to the whole library.
    /// </summary>
    public LibraryStatus? Status { get; init; } = LibraryStatus.Planning;

    /// <summary>Case-insensitive substring match on either title.</summary>
    public string? Search { get; init; }

    // No HiddenOnly (Phase 18b). It listed only the entries somebody had set aside,
    // and setting entries aside is gone: the source list is where "stop offering me
    // this" is said (D11), so there is no local slice left to look at.

    public MediaType? MediaType { get; init; }

    /// <summary>Start of a decade, e.g. 1990 matches 1990–1999.</summary>
    public int? Decade { get; init; }

    /// <summary>
    /// Estimated runtime ceiling in minutes. Entries whose runtime cannot be
    /// estimated are excluded rather than assumed short — an unknown length is not
    /// evidence that something is watchable in an evening.
    /// </summary>
    public int? MaxRuntimeMinutes { get; init; }

    public AnimeSource? Source { get; init; }

    /// <summary>
    /// Only titles with no prequel and no sequel — something self-contained.
    /// </summary>
    /// <remarks>
    /// The surviving half of the brief's franchise/standalone pair, redefined by
    /// D24: there are no franchises to filter for, but "is this a commitment or an
    /// evening" is a real decision, and it sits naturally beside the runtime filter.
    ///
    /// Counted over <b>all</b> edges rather than only owned ones. A series whose
    /// later seasons the user does not own is still a series, and calling it
    /// standalone because of what happens to be in the library would answer a
    /// question about the show with a fact about the collection.
    ///
    /// Only <c>PREQUEL</c> and <c>SEQUEL</c> count. A film with a recap, a spin-off
    /// or an alternative version is still watchable on its own; a season two is not.
    /// </remarks>
    public bool StandaloneOnly { get; init; }

    public int? MinUserScore { get; init; }

    /// <summary>Minimum AI confidence, 0–1. The "high confidence" quick filter.</summary>
    public double? MinRecommendationConfidence { get; init; }

    /// <summary>
    /// True for entries that have an AI score, false for those that do not — the
    /// "not yet ranked" quick filter.
    /// </summary>
    public bool? HasRecommendation { get; init; }

    public LibrarySort Sort { get; init; } = LibrarySort.TitleAscending;

    public int Skip { get; init; }

    public int Take { get; init; } = 50;

    /// <summary>
    /// Whether anything beyond the defaults is narrowing the list.
    /// </summary>
    /// <remarks>
    /// <see cref="Status"/> does not count: it chooses which slice is being looked
    /// at rather than narrowing one, so "clear filters" leaves it alone. Clearing
    /// the slice the user deliberately switched to would make the button unusable
    /// in exactly the place it is most wanted — several filters deep.
    /// </remarks>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(Search)
        || MediaType is not null
        || Decade is not null
        || MaxRuntimeMinutes is not null
        || Source is not null
        || StandaloneOnly
        || MinUserScore is not null
        || MinRecommendationConfidence is not null
        || HasRecommendation is not null;
}

using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>
/// What the library actually contains, used to decide which filters to offer.
///
/// The brief is explicit that a filter should only appear when the metadata behind
/// it exists (§7). A "Movie" filter in a library with no films, or an "under two
/// hours" filter where no title records an episode length, is a control that can
/// only ever return nothing — worse than absent, because the user reasonably reads
/// an empty result as "I own none of these" rather than "this filter is useless".
///
/// Computed by aggregation in the database, never by loading the library.
/// </summary>
public sealed record LibraryFacets
{
    /// <summary>Media types actually present, in enum order. Excludes Unknown.</summary>
    public required IReadOnlyList<MediaType> MediaTypes { get; init; }

    /// <summary>Decades present as start years, e.g. 1990, oldest first.</summary>
    public required IReadOnlyList<int> Decades { get; init; }

    /// <summary>Sources actually represented.</summary>
    public required IReadOnlyList<AnimeSource> Sources { get; init; }

    /// <summary>True when at least one title has both an episode count and a length.</summary>
    public required bool HasRuntimeData { get; init; }

    /// <summary>True when any entry carries an AI score.</summary>
    public required bool HasRecommendations { get; init; }

    /// <summary>True when any entry is missing an AI score — the "not yet ranked" filter.</summary>
    public required bool HasUnrankedEntries { get; init; }

    /// <summary>True when the user has scored anything.</summary>
    public required bool HasUserScores { get; init; }

    /// <summary>True when anything is hidden, so the "show hidden" toggle is worth offering.</summary>
    public required bool HasHiddenEntries { get; init; }

    /// <summary>Count per status, for the status filter's labels.</summary>
    public required IReadOnlyDictionary<LibraryStatus, int> CountByStatus { get; init; }

    public static LibraryFacets Empty { get; } = new()
    {
        MediaTypes = [],
        Decades = [],
        Sources = [],
        HasRuntimeData = false,
        HasRecommendations = false,
        HasUnrankedEntries = false,
        HasUserScores = false,
        HasHiddenEntries = false,
        CountByStatus = new Dictionary<LibraryStatus, int>()
    };
}

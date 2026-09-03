using AniQueue.Core.Domain;

namespace AniQueue.Core.Library;

/// <summary>
/// What the library actually contains, used to decide which filters to offer.
///
/// A filter only appears when the metadata behind it exists. A "Movie" filter in a
/// library with no films, or an "under two
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

    /// <summary>
    /// True when the profile has no library entries at all.
    /// </summary>
    /// <remarks>
    /// The difference between "this filter matched nothing" and "there is nothing
    /// here yet", which a page cannot tell from an empty result: the backlog
    /// defaults to Planning, so a brand new install has a filter applied and its
    /// first screen said *nothing matches those filters* — offering to clear a
    /// filter that was not the reason. Invisible for as long as a seeder
    /// guaranteed there was data.
    /// </remarks>
    public bool IsEmpty => CountByStatus.Count == 0;

    /// <summary>
    /// True when the relation graph holds at least one prequel or sequel edge, so
    /// the standalone filter can exclude something.
    /// </summary>
    /// <remarks>
    /// The exact data the filter reads, rather than "are there any relations at
    /// all". Before the backfill has run — and forever, for a MyAnimeList-only
    /// library — a standalone filter would match every row, which is a control that
    /// appears to work and does nothing. That is the failure this record exists to
    /// prevent, and it is worse here than an empty result: the user reads an
    /// unchanged list as "everything I own is standalone".
    /// </remarks>
    public required bool HasSequelEdges { get; init; }

    /// <summary>
    /// Count per status, for the status filter's labels. A number in a picker is a
    /// promise about what choosing that option shows, so it counts exactly what the
    /// listing behind it lists.
    /// </summary>
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
        HasSequelEdges = false,
        CountByStatus = new Dictionary<LibraryStatus, int>()
    };
}

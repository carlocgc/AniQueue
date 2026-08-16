using AniQueue.Core.Domain;

namespace AniQueue.Core.Import;

/// <summary>
/// One entry as read from an import file, normalised into AniQueue's own terms.
///
/// This is the seam that stops MyAnimeList's (or later AniList's) vocabulary from
/// leaking into the domain: parsers translate into this shape, and everything
/// downstream works from it without knowing which format produced it.
/// </summary>
public sealed record ParsedLibraryEntry
{
    public required AnimeSource Source { get; init; }

    /// <summary>Identifier from the source, when the file provides one.</summary>
    public string? SourceAnimeId { get; init; }

    public required string Title { get; init; }

    public MediaType MediaType { get; init; } = MediaType.Unknown;

    /// <summary>Null when unknown. Sources routinely write 0 to mean "unknown".</summary>
    public int? EpisodeCount { get; init; }

    public LibraryStatus Status { get; init; } = LibraryStatus.Planning;

    public int EpisodesWatched { get; init; }

    /// <summary>1–10, or null when unscored. Never 0 — that is a source convention, not a rating.</summary>
    public int? UserScore { get; init; }

    public DateOnly? DateStarted { get; init; }

    public DateOnly? DateCompleted { get; init; }

    public int TimesRewatched { get; init; }
}

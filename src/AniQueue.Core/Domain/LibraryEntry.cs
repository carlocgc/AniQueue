namespace AniQueue.Core.Domain;

/// <summary>
/// One user's relationship with one title: status, progress, score and notes.
/// Queue membership is not here — it lives in <see cref="QueueItem"/>.
/// </summary>
public class LibraryEntry
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public LibraryStatus Status { get; set; } = LibraryStatus.Planning;

    /// <summary>
    /// The user's own 1–10 rating, or null if unscored. Never assigned
    /// automatically — completing a title only ever offers to record a score.
    /// </summary>
    public int? UserScore { get; set; }

    public int EpisodesWatched { get; set; }

    public DateOnly? DateStarted { get; set; }

    public DateOnly? DateCompleted { get; set; }

    public DateTimeOffset DateAdded { get; set; }

    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Free text. Excluded from AI export unless explicitly opted in.</summary>
    public string? PersonalNotes { get; set; }

    /// <summary>
    /// Which source last wrote the tracking fields above — status, progress, score
    /// and the watch dates. Null for anything edited here rather than observed. It
    /// stops a lower-ranked source overwriting what a higher-ranked one recorded.
    /// </summary>
    public AnimeSource? LastWrittenBySource { get; set; }

    // The currently-applied recommendation, denormalised from the latest applied
    // RecommendationRun so that sorting the backlog by AI score stays a
    // single-table query rather than a join against run history.

    public double? RecommendationScore { get; set; }

    public double? RecommendationConfidence { get; set; }

    public string? RecommendationReason { get; set; }

    public DateTimeOffset? RecommendationUpdatedAt { get; set; }
}

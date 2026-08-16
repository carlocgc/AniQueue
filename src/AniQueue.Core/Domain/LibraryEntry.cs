namespace AniQueue.Core.Domain;

/// <summary>
/// One user's relationship with one title: status, progress, score and notes.
///
/// Note the absence of a QueuePosition column (D1). The brief placed one here,
/// but the Up Next queue must also be able to hold a whole franchise, and no
/// column on this table can express that — a franchise has no LibraryEntry row.
/// Queue membership therefore lives in <see cref="QueueItem"/>.
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
    /// automatically — completing a title only ever *offers* to record a score.
    /// </summary>
    public int? UserScore { get; set; }

    public int EpisodesWatched { get; set; }

    public DateOnly? DateStarted { get; set; }

    public DateOnly? DateCompleted { get; set; }

    public DateTimeOffset DateAdded { get; set; }

    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Free text. Excluded from AI export unless explicitly opted in.</summary>
    public string? PersonalNotes { get; set; }

    /// <summary>User-assigned nudge, independent of any AI ranking. Higher sorts first.</summary>
    public int ManualPriority { get; set; }

    /// <summary>Hidden entries stay in the library but drop out of backlog views.</summary>
    public bool IsHidden { get; set; }

    // Currently-applied recommendation, denormalised from the latest applied
    // RecommendationRun so that sorting the backlog by AI score stays a
    // single-table query rather than a join against run history (D4).

    public double? RecommendationScore { get; set; }

    public double? RecommendationConfidence { get; set; }

    public string? RecommendationReason { get; set; }

    public DateTimeOffset? RecommendationUpdatedAt { get; set; }
}

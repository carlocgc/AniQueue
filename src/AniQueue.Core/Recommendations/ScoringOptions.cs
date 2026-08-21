namespace AniQueue.Core.Recommendations;

/// <summary>
/// The <c>Scoring</c> configuration section, as the application consumes it.
/// </summary>
/// <remarks>
/// A section-bound view rather than the file itself: <c>UserSettings</c> describes
/// what may be written, this describes what scoring needs to read, and binding it
/// to the live section is what lets a save reach an options monitor without a
/// restart (D36).
///
/// These were <c>ProfileSettings</c> columns until D36 moved them. The argument for
/// the move is the one the columns' own documentation already made: the right value
/// is "a property of somebody else's model, which AniQueue cannot see" — an
/// integration detail rather than a display preference, and so the file's side of
/// the line.
/// </remarks>
public class ScoringOptions
{
    /// <summary>Configuration section name, e.g. <c>Scoring:HistorySize</c>.</summary>
    public const string SectionName = "Scoring";

    /// <summary>The most scored titles to send as history. Zero sends none.</summary>
    public int HistorySize { get; set; } = 200;

    /// <summary>The most titles to offer for ranking, or null for all of them.</summary>
    public int? CandidateLimit { get; set; }

    /// <summary>How many rankings to ask for back, or null for one per candidate.</summary>
    public int? ReturnTop { get; set; }

    /// <summary>Whether personal notes travel with a request (§6, opt in).</summary>
    public bool IncludePersonalNotes { get; set; }

    /// <summary>
    /// The bounded form a request is actually built from.
    /// </summary>
    /// <remarks>
    /// Clamping happens here, where the setting is read, rather than where it is
    /// written — so a file edited by hand or left behind by an older build cannot
    /// produce a request nothing can send.
    /// </remarks>
    public ScoringRequestOptions ToRequestOptions() =>
        ScoringRequestOptions.From(HistorySize, CandidateLimit, ReturnTop, IncludePersonalNotes);
}

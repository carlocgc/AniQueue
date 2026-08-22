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
    /// Where a model that speaks the chat-completions API is listening. Empty means
    /// none, which is the normal state of a fresh install.
    /// </summary>
    /// <remarks>
    /// An origin — <c>http://192.168.1.50:1234</c> — rather than a full path. The path
    /// is this application's business and a constant; what the operator knows is where
    /// their server is. Guarded before use (D38), because a settable outbound address
    /// is a request-forgery surface and the guards are what replaced the protection a
    /// constant used to give.
    /// </remarks>
    public string? Endpoint { get; set; }

    /// <summary>Which model to ask for. Passed through verbatim.</summary>
    /// <remarks>
    /// Not validated against anything: the list of models a server has is the server's
    /// business, and a name AniQueue rejected would be a name somebody could not use.
    /// A wrong one comes back as the endpoint's own error, which says more than a
    /// guess of ours would.
    /// </remarks>
    public string? Model { get; set; }

    /// <summary>How long to wait for an answer, in seconds.</summary>
    /// <remarks>
    /// Ten minutes by default, which would be absurd for the AniList client and is
    /// merely generous here: a small model ranking two hundred titles with a sentence
    /// each is generated a word at a time, and the first run on new hardware is the one
    /// most likely to be slow and least likely to be expected.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Whether to ask the server to constrain its output to JSON.
    /// </summary>
    /// <remarks>
    /// On by default, because the servers people actually run — LM Studio, Ollama,
    /// llama.cpp — support it, and a constrained model cannot emit a code fence at all.
    /// That turns D37's unwrapping from the path into the fallback it was meant to be.
    ///
    /// A setting rather than something sniffed, because a server that does not support
    /// it usually answers with an error naming the field, which is a clearer thing to
    /// act on than a retry loop that silently halves what was asked.
    /// </remarks>
    public bool UseStructuredOutput { get; set; } = true;

    /// <summary>
    /// How many further titles must be rated before a score counts as stale (D39).
    /// </summary>
    /// <remarks>
    /// Five by default. One rating is noise; several is a changed picture, and nobody
    /// has to guess at an interval because the interval emerges from how much they
    /// actually watch. Zero turns re-scoring off entirely, which is a legitimate
    /// choice for somebody who wants scores to stay put.
    /// </remarks>
    public int StaleAfterRatings { get; set; } = 5;

    /// <summary>Whether an endpoint has been configured at all.</summary>
    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);

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

using AniQueue.Core.Domain;

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

    /// <summary>The most scored titles to send as history, or null for all of them.</summary>
    public int? HistorySize { get; set; } = 200;

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

    /// <summary>
    /// Whether a scheduled sweep may ask a remote model. Off until asked for.
    /// </summary>
    /// <remarks>
    /// A configuration key for D20's reason: the moment it is needed is the moment
    /// somebody wants a model to stop being hammered, which may be the moment the
    /// pages cannot be reached.
    ///
    /// <b>"It refuses every run, scheduled or pressed" was true and no longer is.</b>
    /// D42 deleted the pressed run, so the only thing left for this to refuse is the
    /// sweep — which is why turning its default off is exactly what "remote ranking is
    /// opt-in" needs, and why no second setting was added beside it. The manual paste
    /// route does not read this and is unaffected.
    /// </remarks>
    public bool Enabled { get; set; }

    // Schedule was here, and its own comment recorded the wart: the type was
    // SyncSchedule and the name "now lies slightly", kept because renaming it would
    // touch every Sources surface for no behavioural gain. Phase 15c removed the
    // question instead of the wart — there is one cadence for every background task
    // now (D40, TaskOptions), so nothing here decides when a sweep happens. What
    // remains below is what a sweep may do once it does.

    /// <summary>
    /// How many titles one unattended batch carries.
    /// </summary>
    /// <remarks>
    /// <b>Was twenty-five, on the reasoning that it is about three thousand output
    /// tokens and so inside any local server's budget.</b> The arithmetic was right
    /// and the assumption under it was not: it counted the tokens of the answer and
    /// not the tokens a reasoning model spends before starting one, and it treated
    /// "fits in the budget" as the only thing that could go wrong with a long reply.
    ///
    /// Measured against gpt-oss-20b at twenty-five, most replies came back short by
    /// choice rather than by truncation — the model simply stopped, which the prompt
    /// permits — so the batch size was setting an answer length the model would not
    /// see through. Ten is inside what it finishes reliably.
    ///
    /// The old note's other half still holds and now cuts the other way: a backlog of
    /// several hundred should still be worked through in an evening. That survives
    /// halving the batch only because the request no longer breaks the prompt cache,
    /// so the history costs one processing per sweep rather than one per batch.
    /// </remarks>
    public int BatchSize { get; set; } = 10;

    /// <summary>How long one sweep may keep going, in minutes.</summary>
    /// <remarks>
    /// Bounded by time rather than by a batch count, because what the operator cares
    /// about is how long their hardware is busy — not how many requests that took. A
    /// sweep that finishes the backlog stops early regardless.
    /// </remarks>
    public int SweepMinutes { get; set; } = 60;

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

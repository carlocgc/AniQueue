namespace AniQueue.Core.Recommendations;

/// <summary>Why an endpoint did not produce a ranking.</summary>
/// <remarks>
/// A closed set rather than a message, because the page says something different for
/// each and a scheduled sweep decides differently too — a model that ran out of room
/// is worth retrying smaller, and a model nothing answered at is not (D25 has
/// enrichment degrade silently; a scoring run has somebody waiting on it and says
/// which side failed).
/// </remarks>
public enum ScoringEndpointFailure
{
    /// <summary>No endpoint is configured, so nothing was attempted.</summary>
    NotConfigured,

    /// <summary>The address was refused before anything was sent (D38).</summary>
    AddressRefused,

    /// <summary>Nothing answered: refused, unreachable, or not listening.</summary>
    Unreachable,

    /// <summary>It answered, but not before the timeout.</summary>
    TimedOut,

    /// <summary>It answered with an HTTP error.</summary>
    Rejected,

    /// <summary>
    /// It began an answer and stopped part-way through, having run out of output room.
    /// </summary>
    /// <remarks>
    /// Worth its own value because it is the failure people hit most and the only one
    /// whose fix is a setting they already have: ask for fewer rankings. Reported as
    /// "not valid JSON" it looks like the model misbehaved, when the model did as well
    /// as it was allowed to.
    /// </remarks>
    Truncated,

    /// <summary>It answered in a shape this build cannot find a reply inside.</summary>
    Unreadable,

    /// <summary>The run was cancelled by whoever started it.</summary>
    Cancelled
}

/// <summary>What asking a model produced.</summary>
/// <remarks>
/// Deliberately not a <c>ScoringPreview</c>. This says what the endpoint did; whether
/// what came back describes this library is a separate question, asked afterwards by
/// <see cref="IRecommendationService.PreviewAsync"/> — the same method the manual path
/// calls, which is what makes this a second courier rather than a second pipeline
/// (D31).
/// </remarks>
public sealed record ScoringEndpointResult
{
    /// <summary>The reply, verbatim, when one arrived.</summary>
    public string? Reply { get; init; }

    /// <summary>What the model called itself, when it said.</summary>
    /// <remarks>
    /// Preferred over the configured name because they differ often enough to matter:
    /// a server configured as <c>local-model</c> may answer as
    /// <c>qwen2.5-14b-instruct</c>, and the second is the one worth recording against
    /// a score somebody may want to revisit on a better model.
    /// </remarks>
    public string? ModelIdentifier { get; init; }

    /// <summary>How long the endpoint took, for the next run to quote as a scale.</summary>
    public TimeSpan Duration { get; init; }

    public ScoringEndpointFailure? Failure { get; init; }

    /// <summary>What went wrong, phrased for the person who has to fix it.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// The first part of what came back, when it can be shown.
    /// </summary>
    /// <remarks>
    /// Bounded and body-only (D38). It is what a person debugging their own server
    /// needs and very little of what a scanner does, which is the trade a settable
    /// address makes necessary.
    /// </remarks>
    public string? Diagnostic { get; init; }

    public bool Succeeded => Failure is null && Reply is not null;

    public static ScoringEndpointResult Success(string reply, string? model, TimeSpan duration) =>
        new() { Reply = reply, ModelIdentifier = model, Duration = duration };

    public static ScoringEndpointResult Failed(
        ScoringEndpointFailure failure,
        string message,
        TimeSpan duration = default,
        string? diagnostic = null) =>
        new() { Failure = failure, Message = message, Duration = duration, Diagnostic = diagnostic };
}

/// <summary>
/// Carries a request to a model the operator hosts, and brings back what it said.
/// </summary>
/// <remarks>
/// <b>It carries; it does not decide.</b> What is sent is what
/// <see cref="ScoringPromptBuilder"/> and <see cref="ScoringRequestWriter"/> produce
/// for the manual path, byte for byte, and what comes back goes to the same parser.
/// Nothing here knows what a good ranking looks like, which is the whole of D31: the
/// contract is Phase 7's and this is a second way to move it.
///
/// <b>It never throws for a failure that is the endpoint's.</b> A self-hosted model
/// being switched off is the normal state of a fresh install, not an exception — so
/// an unreachable address, a timeout and a rejected request all come back as results
/// with a reason on them.
/// </remarks>
public interface IScoringEndpoint
{
    /// <summary>Whether an endpoint is configured at all.</summary>
    /// <remarks>
    /// Asked by the page before it offers to run one, so "no model is set up" is a
    /// state the card describes rather than an error a button produces.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>Where requests go, for the card to show. Null when unconfigured.</summary>
    string? Endpoint { get; }

    /// <summary>The model name requests ask for. Null when unconfigured.</summary>
    string? Model { get; }

    /// <summary>Asks the model to rank the request, and returns what it said.</summary>
    Task<ScoringEndpointResult> AskAsync(
        ScoringRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the smallest request that exercises the whole path, and reports it.
    /// </summary>
    /// <remarks>
    /// A real completion rather than a ping, asking for a two-line ranking of two
    /// invented candidates through the same client and the same output settings. An
    /// endpoint that answers but cannot produce JSON is a distinct outcome from one
    /// that does not answer, and it is the failure that would otherwise only surface
    /// after a ten-minute run.
    /// </remarks>
    Task<ScoringEndpointResult> TestAsync(CancellationToken cancellationToken = default);
}

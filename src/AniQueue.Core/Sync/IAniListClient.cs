namespace AniQueue.Core.Sync;

/// <summary>
/// Everything that came back from one attempt to read a public AniList list.
/// </summary>
/// <remarks>
/// A result rather than an exception, because failure is an outcome a sync has to
/// record rather than an accident: <c>SyncRun</c> stores why a run failed, and a
/// user looking at the Sources page needs "AniList said the account does not
/// exist" rather than a stack trace they cannot see (§6).
///
/// <b>There is no partial success.</b> A fetch that could not be completed carries
/// no payloads at all, because a truncated list is indistinguishable from the user
/// having deleted most of their library — which is the population D19's absence
/// handling acts on.
/// </remarks>
public sealed record AniListFetch
{
    /// <summary>
    /// The raw response bodies, in the order they were fetched. More than one only
    /// when the list was large enough for AniList to chunk it.
    /// </summary>
    /// <remarks>
    /// Bytes rather than open streams. They are already buffered — the size ceiling
    /// has to be enforced while reading, so nothing is streamed past it anyway —
    /// and handing back a list of live streams would leave their disposal to a
    /// caller that has no reason to think about it.
    /// </remarks>
    public IReadOnlyList<byte[]> Payloads { get; init; } = [];

    /// <summary>Null when the fetch completed. Shown to the user as-is, so it must stay plain.</summary>
    public string? FailureReason { get; init; }

    public bool Succeeded => FailureReason is null;

    public static AniListFetch Failed(string reason) => new() { FailureReason = reason };
}

/// <summary>
/// What one relation request returned, and what the server said about how much
/// budget is left.
/// </summary>
/// <remarks>
/// The rate-limit fields are reported rather than acted on. This type holds the
/// socket; the job holds the schedule, and it is the only one of the two with
/// somewhere to wait — the same split that keeps
/// <see cref="IAniListClient.FetchListAsync"/> from retrying inside a user's click.
/// </remarks>
public sealed record AniListRelationsFetch
{
    public byte[]? Payload { get; init; }

    /// <summary>Null when the request completed. Logged rather than shown; nobody is watching.</summary>
    public string? FailureReason { get; init; }

    public bool Succeeded => FailureReason is null;

    /// <summary>
    /// Requests left in the current window, as <c>X-RateLimit-Remaining</c> reports
    /// it, or null when the header was absent or unreadable.
    /// </summary>
    public int? RateLimitRemaining { get; init; }

    /// <summary>How long the server asked us to wait. Only ever set alongside a 429.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static AniListRelationsFetch Failed(string reason, TimeSpan? retryAfter = null) =>
        new() { FailureReason = reason, RetryAfter = retryAfter };
}

/// <summary>
/// Reads a public AniList list over HTTP. The only type in AniQueue that knows
/// GraphQL exists.
/// </summary>
/// <remarks>
/// Declared in Core and implemented in Infrastructure, like every other service
/// boundary here. It deliberately returns bytes rather than parsed entries: parsing
/// is pure and lives in Core where it can be tested without a network, and this
/// side is the part that cannot be (D9, §5).
///
/// Authentication is absent by design. Public lists are readable unauthenticated —
/// verified against the live API — which is what keeps OAuth out of the MVP
/// entirely (D13).
/// </remarks>
public interface IAniListClient
{
    /// <summary>
    /// Fetches every entry on <paramref name="userName"/>'s anime list, following
    /// chunks until the list is complete.
    /// </summary>
    Task<AniListFetch> FetchListAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the relations of up to fifty titles in one request.
    /// </summary>
    /// <remarks>
    /// A separate query rather than a field on the list, and that separation is the
    /// point (D24): relations are near-static while a list changes constantly, so
    /// inlining them would refetch an immutable graph on every poll, inside the
    /// response that also carries the data that does change.
    ///
    /// One request, not a loop. The caller is a background job that has somewhere to
    /// wait between batches, and the pacing decision belongs to it rather than to
    /// the thing holding the socket.
    /// </remarks>
    Task<AniListRelationsFetch> FetchRelationsAsync(
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken = default);
}

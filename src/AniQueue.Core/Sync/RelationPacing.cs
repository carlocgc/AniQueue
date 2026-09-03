namespace AniQueue.Core.Sync;

/// <summary>
/// How long to wait before the next relation request.
///
/// Arithmetic over what the last response reported, with no clock in it, so that
/// pacing can be tested without real time passing. The job does the waiting; this
/// decides how long.
/// </summary>
/// <remarks>
/// The measured limit is 30 requests a minute, not the documented 90. A
/// fifteen-request backfill is therefore half a minute's budget spent in one burst
/// if it is issued as fast as the socket allows — which is how an application
/// discovers a rate limit through 429s rather than through the header that was
/// telling it all along.
///
/// Nothing here is urgent. A backfill that takes two minutes instead of fifteen
/// seconds is indistinguishable to a user who is not watching it, so the spacing is
/// deliberately generous rather than the fastest that would not be refused.
/// </remarks>
public static class RelationPacing
{
    /// <summary>One request every two seconds — the measured limit, spread evenly.</summary>
    public static readonly TimeSpan BetweenRequests = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Where "nearly out" begins. Below this the pass stops spreading and starts
    /// waiting for the window to roll over.
    /// </summary>
    /// <remarks>
    /// Five rather than one, because the header describes the window at the moment
    /// the server answered and anything else sharing the account's budget — a user
    /// pressing Sync Now — spends from the same pool.
    /// </remarks>
    public const int LowRemaining = 5;

    /// <summary>Long enough for a per-minute window to have rolled over.</summary>
    public static readonly TimeSpan WindowReset = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wait before the next request, given what the last one reported.
    /// </summary>
    /// <remarks>
    /// <paramref name="retryAfter"/> wins outright when present: it is the server
    /// stating a number rather than this application inferring one, and a 429 is the
    /// one case where guessing shorter is actively harmful. It is clamped to at
    /// least the ordinary spacing so a <c>Retry-After: 0</c> cannot turn into a busy
    /// loop.
    ///
    /// An absent <c>X-RateLimit-Remaining</c> is treated as "no information" and
    /// paced normally rather than pessimistically. The header is not guaranteed, and
    /// waiting a minute between every request because a proxy stripped it would turn
    /// a half-minute backfill into a twelve-hour one.
    /// </remarks>
    public static TimeSpan DelayBefore(int? remaining, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } wait)
        {
            return wait > BetweenRequests ? wait : BetweenRequests;
        }

        return remaining is { } left && left <= LowRemaining ? WindowReset : BetweenRequests;
    }
}

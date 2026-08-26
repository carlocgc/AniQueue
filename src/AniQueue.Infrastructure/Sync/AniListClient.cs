using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AniQueue.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Reads a public AniList list over GraphQL.
///
/// A GraphQL request is an HTTP POST with a JSON body, which is why no client
/// library was taken for it (§12): <c>HttpClient</c> and <c>System.Text.Json</c>
/// are in the box, and the one query this application issues is written out below
/// where it can be read.
/// </summary>
public sealed class AniListClient(HttpClient httpClient, ILogger<AniListClient> logger) : IAniListClient
{
    private const string Endpoint = "https://graphql.anilist.co";

    /// <summary>
    /// AniList's documented maximum. Asked for explicitly rather than left to the
    /// server's default so the chunk loop below has defined arithmetic.
    /// </summary>
    private const int PerChunk = 500;

    /// <summary>
    /// 20 chunks — 10,000 entries — before the fetch is abandoned as unbounded.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a trusted loop condition: <c>hasNextChunk</c> comes
    /// from the other end, and a server that always says "there is more" would
    /// otherwise be a request loop this application never escapes. Hitting it is a
    /// failure rather than a truncation, because half a list is the one shape a
    /// sync must never act on (§5, D19).
    /// </remarks>
    private const int MaxChunks = 20;

    /// <summary>
    /// The only query AniQueue issues.
    /// </summary>
    /// <remarks>
    /// Two arguments carry decisions rather than mechanics:
    ///
    /// <c>type: ANIME</c> pins the collection, so a manga format never arrives and
    /// the parser's guard against one stays theoretical.
    ///
    /// <c>score(format: POINT_100)</c> asks the server to convert from whichever of
    /// five scoring systems the account uses. POINT_100 specifically, not POINT_10:
    /// AniList rounds during conversion, so a 100-point user's 4 would come back as
    /// 0 and be indistinguishable from unscored. The finest-grained integer scale
    /// loses nothing and leaves the 1–10 mapping to the parser, where it is tested.
    ///
    /// Everything else is the smallest set the pipeline consumes. <c>relations</c>
    /// is deliberately absent, and stays absent: relations are near-static while a
    /// list changes constantly, so they belong to a separate, lazy pass rather than
    /// riding along on every poll of the data that does change (D24).
    ///
    /// <b>Phase 9b's four fields ride along, and that is the whole of the phase's
    /// fetching</b> (D49). <c>description</c>, <c>genres</c>, <c>studios</c> and
    /// <c>coverImage.extraLarge</c> arrive in a request that was already being made
    /// for the entire collection, so every existing title is populated by the next
    /// scheduled sync and no backfill pass is needed — unlike relations, which needed
    /// one precisely because they are not here.
    ///
    /// <c>description</c> is taken without <c>asHtml</c> deliberately. AniList's own
    /// markdown keeps spoilers as <c>~!...!~</c>, which a renderer can mask; the HTML
    /// form has already expanded them into markup, and would additionally be
    /// third-party HTML that only <c>MarkupString</c> could render (D49).
    /// </remarks>
    private const string ListQuery =
        """
        query ($userName: String, $chunk: Int, $perChunk: Int) {
          MediaListCollection(userName: $userName, type: ANIME, chunk: $chunk, perChunk: $perChunk) {
            hasNextChunk
            lists {
              name
              isCustomList
              entries {
                status
                score(format: POINT_100)
                progress
                repeat
                updatedAt
                startedAt { year month day }
                completedAt { year month day }
                media {
                  id
                  idMal
                  type
                  format
                  episodes
                  duration
                  seasonYear
                  description
                  genres
                  title { romaji english native }
                  coverImage { medium extraLarge }
                  studios { edges { isMain node { name isAnimationStudio } } }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The relation query (D24).
    /// </summary>
    /// <remarks>
    /// <c>type: ANIME</c> pins the media selected, but deliberately not the far end
    /// of an edge — a relation node is whatever the source says it is, and asking
    /// for its <c>type</c> is what lets the parser drop manga rather than store an
    /// edge pointing at something this application will never hold.
    ///
    /// <c>startDate</c> and <c>coverImage.color</c> ride along because the request is
    /// being made anyway: release ordering needs a date finer than a year, and the
    /// colour is six bytes Phase 9 will want (D25). Neither justifies a request of
    /// its own, which is exactly why they are here and not in a pass of their own.
    ///
    /// <c>idMal</c> is asked for on the media but not on the node. It costs nothing
    /// on a row that is already being written, and it is not read on the far end
    /// because storing an edge under two identities would claim AniList published a
    /// MyAnimeList relationship, which it did not.
    /// </remarks>
    private const string RelationsQuery =
        """
        query ($ids: [Int]) {
          Page(page: 1, perPage: 50) {
            media(id_in: $ids, type: ANIME) {
              id
              idMal
              startDate { year month day }
              coverImage { color }
              relations {
                edges {
                  relationType
                  node { id type }
                }
              }
            }
          }
        }
        """;

    /// <summary>The most ids one request may carry — AniList's page ceiling.</summary>
    public const int MaxRelationIdsPerRequest = 50;

    public async Task<AniListRelationsFetch> FetchRelationsAsync(
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(externalIds);

        // Parsed here rather than passed through as text: the variable is typed
        // [Int] on the other end, and an identifier that is not a number is one this
        // query could never have answered for. AnimeExternalId keeps identifiers as
        // text precisely because they arrive from files and are not trusted to be
        // numeric (D17), so this is where that assumption gets checked.
        var ids = externalIds
            .Select(id => int.TryParse(id, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .Take(MaxRelationIdsPerRequest)
            .ToArray();

        if (ids.Length == 0)
        {
            return AniListRelationsFetch.Failed("No AniList identifiers were given.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { query = RelationsQuery, variables = new { ids } })
        };

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            var remaining = ReadRemaining(response);

            if (!response.IsSuccessStatusCode)
            {
                // Read for the same reason the list fetch reads it: a GraphQL server
                // explains itself in the body, not in the status.
                var failure = await response.Content.ReadAsStringAsync(cancellationToken);

                return new AniListRelationsFetch
                {
                    FailureReason = DescribeFailure(response, failure),
                    RateLimitRemaining = remaining,

                    // Only meaningful alongside a 429, and only then does the server
                    // send it. Read unconditionally so a rate limit expressed with
                    // some other status still paces correctly.
                    RetryAfter = response.Headers.RetryAfter?.Delta
                };
            }

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return new AniListRelationsFetch { Payload = payload, RateLimitRemaining = remaining };
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "AniList relation request failed for {Count} titles", ids.Length);
            return AniListRelationsFetch.Failed("AniList could not be reached.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AniList relation request timed out for {Count} titles", ids.Length);
            return AniListRelationsFetch.Failed("AniList did not respond in time.");
        }
    }

    /// <summary>
    /// Reads <c>X-RateLimit-Remaining</c>, or null when it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// Absence is reported as absence rather than as zero. Zero means "stop", and a
    /// proxy that strips the header would otherwise halt a backfill that nothing was
    /// actually refusing.
    /// </remarks>
    private static int? ReadRemaining(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
        int.TryParse(values.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var remaining)
            ? remaining
            : null;

    public async Task<AniListFetch> FetchListAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return AniListFetch.Failed("No AniList account is configured.");
        }

        var payloads = new List<byte[]>();

        for (var chunk = 1; chunk <= MaxChunks; chunk++)
        {
            var (payload, failure) = await FetchChunkAsync(userName, chunk, cancellationToken);

            if (failure is not null)
            {
                return AniListFetch.Failed(failure);
            }

            payloads.Add(payload!);

            if (!HasNextChunk(payload!))
            {
                logger.LogInformation(
                    "Fetched an AniList list in {Chunks} request(s), {Bytes} bytes",
                    chunk,
                    payloads.Sum(p => p.Length));

                return new AniListFetch { Payloads = payloads };
            }
        }

        return AniListFetch.Failed(
            $"The list did not finish within {MaxChunks} requests, so nothing was applied.");
    }

    private async Task<(byte[]? Payload, string? Failure)> FetchChunkAsync(
        string userName,
        int chunk,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                query = ListQuery,
                variables = new { userName, chunk, perChunk = PerChunk }
            })
        };

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Read before describing, because AniList says why in the body rather
                // than in the status. It answered a 403 with "The AniList API has been
                // temporarily disabled due to severe stability issues", and the page
                // said "AniList returned 403." — a number the user can do nothing with
                // in place of a sentence that explains itself (D40, §6).
                var failure = await response.Content.ReadAsStringAsync(cancellationToken);

                return (null, DescribeFailure(response, failure));
            }

            // Read as bytes rather than a stream: HttpClient's
            // MaxResponseContentBufferSize enforces the ceiling here, and the
            // response has to be buffered anyway to read hasNextChunk before
            // deciding whether to ask for more.
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return (payload, null);
        }
        catch (HttpRequestException ex)
        {
            // Deliberately not surfaced verbatim. The message can carry the
            // resolved host and inner socket detail, and §6 keeps that out of a
            // production-facing surface; the log has the exception in full.
            logger.LogWarning(ex, "AniList request failed for chunk {Chunk}", chunk);
            return (null, "AniList could not be reached.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AniList request timed out for chunk {Chunk}", chunk);
            return (null, "AniList did not respond in time.");
        }
    }

    /// <summary>
    /// Turns a non-success response into something worth showing the user.
    /// </summary>
    /// <remarks>
    /// 404 is the one worth naming: it is what a mistyped username or a list turned
    /// private looks like, and it is the failure an operator can actually fix.
    ///
    /// The measured rate limit is 30 requests a minute, not the documented 90, so
    /// 429 is real rather than theoretical — though a single on-demand fetch of one
    /// or two requests is a long way from it. <b>No retry happens here.</b>
    /// Retrying inside a user-initiated action turns a fast failure into a stall
    /// they cannot cancel; backoff belongs to the unattended runner in Phase 5c,
    /// which has somewhere to wait.
    ///
    /// <b>Anything else asks AniList what it meant.</b> A GraphQL server answers a
    /// failure with an <c>errors</c> array whatever the status, and that message is
    /// the only one that can describe a state nobody anticipated — an API switched
    /// off for maintenance being the case that prompted this. Falling back to the
    /// status code is what happens when it says nothing useful.
    /// </remarks>
    private static string DescribeFailure(HttpResponseMessage response, string body) => response.StatusCode switch
    {
        HttpStatusCode.NotFound =>
            "AniList has no such user, or the list is private.",

        HttpStatusCode.TooManyRequests =>
            response.Headers.RetryAfter?.Delta is { } wait
                ? $"AniList is rate limiting requests. Try again in {(int)wait.TotalSeconds} seconds."
                : "AniList is rate limiting requests. Try again shortly.",

        _ => Explanation(body) is { } said
            ? $"AniList says: {said}"
            : $"AniList returned {(int)response.StatusCode}."
    };

    /// <summary>
    /// The first message out of a GraphQL <c>errors</c> array, if there is one.
    /// </summary>
    /// <remarks>
    /// <b>Bounded and parsed rather than pasted.</b> §6 treats what a remote host
    /// sends back as untrusted and caps what an endpoint may say — the same rule the
    /// scoring client follows — so this reads one string out of a known shape and
    /// truncates it, rather than surfacing whatever arrived. A body that is not JSON,
    /// or is JSON of some other shape, produces nothing and the caller falls back to
    /// the status code.
    /// </remarks>
    private static string? Explanation(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var error in errors.EnumerateArray())
            {
                if (error.TryGetProperty("message", out var message)
                    && message.GetString() is { Length: > 0 } text)
                {
                    return text.Length > 200 ? text[..200] : text;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, or not the shape this expects. The status code still says
            // something true, which is better than saying something wrong.
        }

        return null;
    }

    /// <summary>
    /// Reads the paging flag, and nothing else, out of the response.
    /// </summary>
    /// <remarks>
    /// This is the one piece of the body the client looks at, and it is protocol
    /// rather than content: whether to issue another request. Everything meaningful
    /// in the payload is the parser's business, in Core, where it is tested without
    /// a network (D9).
    ///
    /// An unreadable body returns false rather than throwing. The parser is about
    /// to reject it with a message describing what is actually wrong, which is a
    /// better error than one about paging.
    /// </remarks>
    private static bool HasNextChunk(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("MediaListCollection", out var collection) &&
                   collection.ValueKind == JsonValueKind.Object &&
                   collection.TryGetProperty("hasNextChunk", out var flag) &&
                   flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

using AniQueue.Core.Artwork;
using AniQueue.Infrastructure.Artwork;

namespace AniQueue.Web.Endpoints;

/// <summary>
/// Serves the cached pictures under <c>/data</c> (D47).
/// </summary>
/// <remarks>
/// <b>An endpoint rather than static file middleware</b>, for three reasons that all
/// come back to the same one: pointing <c>UseStaticFiles</c> at the cache directory
/// would make the filesystem layout a public contract, would serve whatever happened
/// to be in there by name, and would give away the ability to decide what a miss
/// means. This decides: a miss is a 404, the page has already rendered a colour block
/// rather than an <c>img</c> for anything it knows is not cached, and the job repairs
/// the row on its next pass.
///
/// <b>No database is touched.</b> The route carries everything — the kind, the
/// title's id, the content hash and the extension — so a backlog page with fifty
/// covers costs fifty file reads and no queries. That is only safe because every
/// segment is matched against a whitelist rather than sanitised: one of four kind
/// names, an integer, and hexadecimal followed by a known image extension. §6 forbids
/// user-supplied file paths, and this is how that is kept true.
/// </remarks>
public static class CoverArtEndpoint
{
    /// <summary>
    /// A year, and <c>immutable</c>, which the hash in the URL is what earns.
    /// </summary>
    /// <remarks>
    /// Replaced art arrives at a different address, so a cached copy can never be
    /// stale — there is nothing for a revalidation to discover. Without the hash this
    /// would have to be a short lifetime plus an <c>ETag</c>, which is fifty
    /// conditional requests every time somebody opens the backlog.
    /// </remarks>
    private const string CacheControl = "public, max-age=31536000, immutable";

    public static IEndpointRouteBuilder MapCachedCoverArt(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            $"/{ArtworkPaths.Root}/{{kind}}/{{animeId:int}}/{{segment}}",
            (string kind, int animeId, string segment, CoverArtStore store, HttpContext httpContext) =>
            {
                if (!ArtworkPaths.TryParseKind(kind, out var imageKind)
                    || !ArtworkPaths.TryParseSegment(segment, out var contentHash, out var fileExtension)
                    || ImageSource.ContentTypeFor(fileExtension) is not { } contentType)
                {
                    return Results.NotFound();
                }

                var content = store.OpenRead(imageKind, animeId, contentHash, fileExtension);
                if (content is null)
                {
                    // Deliberately without the cache header below. A miss means the
                    // job has not fetched this yet or the file has gone, and both
                    // repair themselves within a tick — a 404 cached for a year would
                    // outlive the repair by about a year.
                    return Results.NotFound();
                }

                httpContext.Response.Headers.CacheControl = CacheControl;
                return Results.Stream(content, contentType);
            })
            // Nothing here reads or writes anything a form could be forged into, and
            // an image element cannot carry a token — so the antiforgery middleware
            // has nothing to check and would refuse every request if it tried.
            .DisableAntiforgery();

        return endpoints;
    }
}

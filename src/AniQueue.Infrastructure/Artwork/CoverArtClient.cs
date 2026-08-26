using AniQueue.Core.Artwork;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Artwork;

/// <summary>
/// Asks a CDN for one picture, and decides what its answer means (D47).
/// </summary>
/// <remarks>
/// <b>Every guard §6 asks for is in one method here.</b> The host is checked before a
/// socket is opened, the scheme must be https, a redirect is refused rather than
/// followed, the body must declare itself an image, and the size is capped twice —
/// once against the declared length and once by the transport, which refuses an
/// oversized body while it is still arriving.
///
/// Separate from the pass that calls it so those checks can be tested against a stub
/// transport with no database in sight, and so a single long-lived client can be a
/// singleton while the pass around it stays scoped.
/// </remarks>
public sealed class CoverArtClient(HttpClient httpClient, ILogger<CoverArtClient> logger) : ICoverArtClient
{
    public async Task<CoverArtFetch> FetchAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        if (!ImageSource.IsAllowed(remoteUrl))
        {
            // Logged without the address. It came from a third party and this is the
            // path where that mattered, so repeating it into the log is the one thing
            // worth not doing with it.
            logger.LogWarning("Refusing to fetch cover art from an address that is not allowed");
            return CoverArtFetch.Permanent;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // The allowlist vouches for the address that was asked for. It can
                // vouch for nothing about where that address points, so this is a
                // refusal rather than a hop.
                logger.LogWarning("Refusing to follow a redirect while fetching cover art");
                return CoverArtFetch.Permanent;
            }

            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode >= 500 || (int)response.StatusCode == 429
                    ? CoverArtFetch.Transient
                    : CoverArtFetch.Permanent;
            }

            // What the server says it sent decides, never the URL's path — the path
            // is the part somebody else controls, and a suffix cannot promise a type.
            var extension = ImageSource.ExtensionFor(response.Content.Headers.ContentType?.MediaType);
            if (extension is null)
            {
                return CoverArtFetch.Permanent;
            }

            if (response.Content.Headers.ContentLength > ImageSource.MaxByteCount)
            {
                return CoverArtFetch.Permanent;
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // An empty body is a permanent problem rather than a picture: writing it
            // would cache a zero-byte file under an immutable URL and never look at
            // it again.
            return content.Length is 0 || content.Length > ImageSource.MaxByteCount
                ? CoverArtFetch.Permanent
                : CoverArtFetch.Success(content, extension);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The pass was stopped rather than the fetch failing. Rethrown so nothing
            // is recorded against the row, which is what makes Cancel free rather
            // than something that costs the title an attempt.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogDebug(exception, "Cover art fetch failed");
            return CoverArtFetch.Transient;
        }
    }
}

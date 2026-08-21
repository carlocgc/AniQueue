using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace AniQueue.Core.Recommendations;

/// <summary>
/// Decides whether an address AniQueue was told to POST to may be POSTed to (D38).
/// </summary>
/// <remarks>
/// §6 used to say every outbound endpoint is a constant held in code, so there was no
/// request-forgery surface at all. D36 made the scoring endpoint a setting a page can
/// write, because a self-hosted model has no address anybody but the operator knows —
/// and this is what replaced the protection the constant gave.
///
/// <b>What it is protecting against, stated plainly.</b> AniQueue has no
/// authentication, so whoever can reach the port can already read the library. What a
/// settable outbound address adds is reach into places the surrounding network cannot
/// touch — loopback, the container network, and the cloud metadata service — and the
/// diagnostic that reports a failing endpoint turns that reach into a way to read the
/// answers. Neither half is worth much alone.
///
/// <b>What it deliberately allows.</b> Loopback and private ranges, because that is
/// exactly where a self-hosted model lives. Refusing them would be theatre: reaching
/// the page at all means already being on that network, so the SSRF grants no reach
/// the caller did not have. What it cannot grant is the container's own network and
/// link-local — hence the one range that is refused.
///
/// <b>Honest limitation:</b> none of this is a boundary until Phase 12's optional
/// login exists. It is taken now because it costs nothing while nobody has an endpoint
/// saved, and because Phase 14 should find a decision rather than an open question.
/// </remarks>
public static class ScoringEndpointAddress
{
    /// <summary>The path every chat-completions server serves. Never configurable.</summary>
    /// <remarks>
    /// The operator supplies an origin and AniQueue supplies the path, which keeps the
    /// part that identifies the protocol out of the part somebody types. It also means
    /// a pasted full URL with a path on it is corrected rather than honoured.
    /// </remarks>
    public const string CompletionsPath = "/v1/chat/completions";

    /// <summary>
    /// Turns a configured origin into the URL to POST to, or says why it will not.
    /// </summary>
    public static bool TryResolve(
        string? endpoint,
        [NotNullWhen(true)] out Uri? target,
        [NotNullWhen(false)] out string? refusal)
    {
        target = null;
        refusal = null;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            refusal = "No model endpoint is configured.";
            return false;
        }

        var text = endpoint.Trim();

        // A bare host:port is what somebody reads off LM Studio's own screen, so it is
        // completed rather than refused. http rather than https because the target is
        // a machine on their own network, which is where the scheme check below stops
        // this from being a licence to reach anything else.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = $"http://{text}";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            refusal = $"\"{endpoint}\" is not an address AniQueue can read.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            refusal = $"The endpoint must be http or https. \"{uri.Scheme}\" is not.";
            return false;
        }

        // A URL carrying credentials exists to smuggle authentication somewhere, and
        // no self-hosted model needs any. Refused rather than stripped, because
        // stripping would send the request to the host anyway and silently drop the
        // half the operator thought was doing something.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            refusal = "The endpoint must not carry a username or password.";
            return false;
        }

        if (IsLinkLocal(uri))
        {
            // 169.254.0.0/16, which on a hosted machine is where the metadata service
            // hands out credentials to anyone who asks. It is the only address in this
            // picture that reaches something genuinely privileged, and no model has
            // ever been served from it.
            refusal = "The endpoint must not be a link-local address.";
            return false;
        }

        // The path is ours. Anything the operator put on the end of the origin — a
        // trailing slash, or a whole /v1/chat/completions pasted from documentation —
        // is discarded rather than concatenated into something that would 404.
        target = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, CompletionsPath).Uri;

        return true;
    }

    private static bool IsLinkLocal(Uri uri) =>
        IPAddress.TryParse(uri.Host, out var address)
        && (address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().GetAddressBytes() is [169, 254, ..]
            : address.GetAddressBytes() is [169, 254, ..] || address.IsIPv6LinkLocal);
}

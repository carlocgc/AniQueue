using AniQueue.Core.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace AniQueue.Web.Security;

/// <summary>
/// Re-checks a live circuit's stamp, so signing the other devices out reaches the
/// tab that is already open rather than waiting for it to be reloaded.
/// </summary>
/// <remarks>
/// Authorisation is decided on the HTTP request that delivered the page; after
/// that the circuit holds the principal it was given, and a cookie thrown away by
/// <see cref="AniQueueAuth.ValidateStampAsync"/> never reaches it. Without this, a
/// password change would leave every open tab working until somebody reloaded one
/// — which is the opposite of what changing a password is for.
/// </remarks>
public sealed class StampAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IAuthService auth) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <summary>
    /// How long a signed-out session may keep an open tab. The check compares two
    /// strings already in memory, so the interval is not about cost — it is the
    /// gap between changing a password and the tab in the other room noticing.
    /// </summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);

        // Nothing to revalidate against while there is no password. An open tab on
        // an unlocked application is not a session, and ending it would log out a
        // user who never logged in.
        if (!await auth.IsLockedAsync(cancellationToken))
        {
            return true;
        }

        var stamp = authenticationState.User.FindFirst(AniQueueAuth.StampClaim)?.Value;

        return await auth.IsStampCurrentAsync(stamp, cancellationToken);
    }
}

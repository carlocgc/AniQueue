using System.Security.Claims;
using AniQueue.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace AniQueue.Web.Security;

/// <summary>
/// The names and the rules the optional lock is wired from.
/// </summary>
/// <remarks>
/// Cookie sign-in from the shared framework rather than ASP.NET Core Identity,
/// which would bring a package, user and role tables and a set of pages for an
/// application with exactly one account and no registration.
/// </remarks>
public static class AniQueueAuth
{
    /// <summary>The one authentication scheme.</summary>
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// What the cookie is called. Named for the application rather than left as the
    /// framework default, so an operator reading their own browser storage can see
    /// which of their self-hosted things put it there.
    /// </summary>
    public const string CookieName = "aniqueue_session";

    /// <summary>
    /// The claim carrying the profile's stamp, which is what lets a cookie be
    /// retired without waiting for it to expire.
    /// </summary>
    public const string StampClaim = "aniqueue:stamp";

    /// <summary>
    /// What the single account is called wherever a name is needed. There is no
    /// username to sign in with; this exists so the principal is not nameless.
    /// </summary>
    public const string AccountName = "owner";

    /// <summary>Builds the principal a successful sign-in is issued for.</summary>
    public static ClaimsPrincipal PrincipalFor(string stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, AccountName), new Claim(StampClaim, stamp)],
            Scheme);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Checks a cookie's stamp on every request it arrives on, and throws the
    /// cookie away when the password has been set, changed or cleared since.
    /// </summary>
    /// <remarks>
    /// The check costs nothing per request: the service holds the stamp in memory
    /// and owns the only writes to it.
    /// </remarks>
    public static async Task ValidateStampAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var auth = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
        var stamp = context.Principal?.FindFirst(StampClaim)?.Value;

        if (await auth.IsStampCurrentAsync(stamp, context.HttpContext.RequestAborted))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(Scheme);
    }
}

/// <summary>
/// Passes when there is nothing to unlock, or when the caller has signed in.
/// </summary>
/// <remarks>
/// A requirement rather than the framework's plain "must be authenticated",
/// because whether this application is locked is a question about the database and
/// not about the request — and the answer changes the moment somebody sets or
/// clears a password, without a restart.
/// </remarks>
public sealed class UnlockedRequirement : IAuthorizationRequirement;

/// <summary>Answers <see cref="UnlockedRequirement"/>.</summary>
public sealed class UnlockedHandler(IAuthService auth) : AuthorizationHandler<UnlockedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        UnlockedRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated is true || !await auth.IsLockedAsync())
        {
            context.Succeed(requirement);
        }
    }
}

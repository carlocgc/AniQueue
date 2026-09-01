namespace AniQueue.Core.Security;

/// <summary>
/// The optional lock: whether there is a password, and everything that changes it.
/// </summary>
/// <remarks>
/// <b>The password is the switch.</b> There is no separate setting to turn a login
/// on, because <i>on with no password</i> is a state that must not exist and a
/// second control is what would create it. Setting a password locks the
/// application; clearing it opens it again.
///
/// There is no account name. One account means a username field with one possible
/// value, which cannot be wrong and so defends nothing.
/// </remarks>
public interface IAuthService
{
    /// <summary>Whether a password is set, and so whether anything is locked.</summary>
    Task<bool> IsLockedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a password. The stamp to put in the cookie when it matches, null
    /// when it does not.
    /// </summary>
    Task<string?> SignInAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or replaces the password and mints a new stamp, which is what signs
    /// every other device out. The stamp for the caller's own new cookie.
    /// </summary>
    Task<string> SetPasswordAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the password, so nothing is locked. Mints a new stamp too: the
    /// cookies issued under the old one are no longer anybody's business.
    /// </summary>
    Task ClearPasswordAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a cookie's stamp is still the current one. False once the password
    /// has been set, changed or cleared since the cookie was issued.
    /// </summary>
    Task<bool> IsStampCurrentAsync(string? stamp, CancellationToken cancellationToken = default);
}

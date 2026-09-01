namespace AniQueue.Core.Security;

/// <summary>
/// What a change to the lock produced.
/// </summary>
/// <param name="Stamp">
/// The stamp for the caller's own new cookie, or null when the change left nobody
/// signed in.
/// </param>
/// <param name="SettingsError">
/// Why <c>Auth:Enabled</c> could not be written, phrased for the person who has to
/// fix it, or null when it was. The password half of the change has already
/// happened when this is set — a non-root container writing to a root-owned bind
/// mount is a real deployment, and a page that threw there would turn a settings
/// edit into an error page rather than into an explanation.
/// </param>
public sealed record AuthChange(string? Stamp, string? SettingsError);

/// <summary>
/// The optional lock: whether AniQueue asks for a password, and everything that
/// changes one.
/// </summary>
/// <remarks>
/// <b>The switch and the password move together, and this is what moves them.</b>
/// <c>Auth:Enabled</c> lives in <c>userconfig.json</c> because it is the half an
/// operator has to be able to reach when the pages are the thing locking them out;
/// the hash lives on the profile row because a credential is not written to a file
/// in plain text. Setting a password turns the switch on and clearing it turns the
/// switch off, so the two disagree only when somebody edits the file by hand — and
/// <see cref="AuthState"/> gives each of those two readings an answer.
///
/// There is no account name. One account means a username field with one possible
/// value, which cannot be wrong and so defends nothing.
/// </remarks>
public interface IAuthService
{
    /// <summary>What the lock is currently doing.</summary>
    Task<AuthState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a password. The stamp to put in the cookie when it matches, null when
    /// it does not.
    /// </summary>
    Task<string?> SignInAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a password, turns the switch on, and mints a new stamp — which is what
    /// signs every other device out.
    /// </summary>
    Task<AuthChange> SetPasswordAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the password and turns the switch off, so nothing is locked. Mints a
    /// new stamp too: the cookies issued under the old one are no longer anybody's
    /// business.
    /// </summary>
    Task<AuthChange> RemovePasswordAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets a stored password when the switch is off, and says whether it found
    /// one. The way back in after a forgotten password, and the reason turning the
    /// setting off in the file is enough on its own.
    /// </summary>
    /// <remarks>
    /// Run at startup rather than per request, because it writes. A start is also
    /// the only moment an operator's hand edit can have arrived without the
    /// application having been the one to make it.
    /// </remarks>
    Task<bool> ForgetPasswordIfDisabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a cookie's stamp is still the current one. False once the password
    /// has been set, changed or removed since the cookie was issued.
    /// </summary>
    Task<bool> IsStampCurrentAsync(string? stamp, CancellationToken cancellationToken = default);
}

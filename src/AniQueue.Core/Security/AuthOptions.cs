namespace AniQueue.Core.Security;

/// <summary>
/// The <c>Auth</c> configuration section, as the application consumes it.
/// </summary>
/// <remarks>
/// Bound to the live section like the other option types here, so turning the lock
/// on or off reaches a running application without a restart.
///
/// One key, and it is the switch. The password itself is not configuration: a
/// credential is not written to a file an operator opens in an editor.
/// </remarks>
public class AuthOptions
{
    /// <summary>Configuration section name, e.g. <c>Auth:Enabled</c>.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// Whether AniQueue asks for a password. Off by default, and off is a supported
    /// deployment rather than a state to be nagged out of.
    /// </summary>
    /// <remarks>
    /// False here is also the way back in after a forgotten password: a start that
    /// finds this off forgets any stored password, so the file an operator can still
    /// reach is what unlocks an application whose pages they cannot.
    /// </remarks>
    public bool Enabled { get; set; }
}

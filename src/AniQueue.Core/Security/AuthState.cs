namespace AniQueue.Core.Security;

/// <summary>
/// What the lock is doing, which is the two settings it is made of read together.
/// </summary>
public enum AuthState
{
    /// <summary>
    /// <c>Auth:Enabled</c> is off. Nothing asks for anything, which is how a fresh
    /// installation runs and how one stays until somebody asks otherwise.
    /// </summary>
    Open,

    /// <summary>
    /// Switched on with no password stored. Nobody is locked out, because there is
    /// nothing to be locked out of yet — so every page sends the visitor to set one
    /// rather than pretending to be protected.
    /// </summary>
    /// <remarks>
    /// Reachable two ways: an operator turning it on in the file before ever opening
    /// the application, and a container started with the setting already true. Both
    /// want the same answer.
    /// </remarks>
    NeedsPassword,

    /// <summary>Switched on with a password stored. Everything asks.</summary>
    Locked
}

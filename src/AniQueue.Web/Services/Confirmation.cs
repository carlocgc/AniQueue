namespace AniQueue.Web.Services;

/// <summary>
/// A confirmation carried from a page that redirects to the page it lands on.
/// </summary>
/// <remarks>
/// <b>For a redirect that crosses a request.</b> A toast normally belongs to the page
/// that raised it, and that is still the rule; this exists for the one case where the
/// page doing the work cannot show it — the password forms are statically rendered,
/// because a cookie is written on a response, so they finish by sending the user
/// somewhere else entirely.
///
/// The name rather than the wording travels, so the page that lands does not have to
/// carry copy about a feature it knows nothing about, and an address somebody has
/// bookmarked cannot put arbitrary text on their screen.
/// </remarks>
public static class Confirmation
{
    /// <summary>The query parameter carrying the name.</summary>
    public const string Parameter = "saved";

    /// <summary>A password was set on an installation that had none.</summary>
    public const string PasswordSet = "password-set";

    /// <summary>An existing password was replaced.</summary>
    public const string PasswordChanged = "password-changed";

    /// <summary>The password was removed, so nothing is locked.</summary>
    public const string PasswordRemoved = "password-removed";

    /// <summary>
    /// What to say for a name, or null for one this build does not know — an old
    /// link, or somebody typing in the address bar.
    /// </summary>
    public static string? MessageFor(string? name) => name switch
    {
        PasswordSet => "Password set. AniQueue is locked now.",
        PasswordChanged => "Password changed. Other devices signed out.",
        PasswordRemoved => "Password removed. AniQueue is open.",
        _ => null
    };
}

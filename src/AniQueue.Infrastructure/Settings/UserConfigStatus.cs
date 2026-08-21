namespace AniQueue.Infrastructure.Settings;

/// <summary>
/// Whether the operator's settings file could be read, so the application can say
/// so instead of failing to start.
/// </summary>
/// <remarks>
/// A malformed <c>userconfig.json</c> used to be fatal: the configuration provider
/// throws while the host is being built, before logging exists, so a missing comma
/// produced a stack trace on the console and no application. That is the worst
/// possible behaviour for the one file an operator edits by hand, usually while
/// fixing something else, and often over SSH at an unreasonable hour (D20).
///
/// So the file is now allowed to fail. What it configures is skipped, everything
/// else starts normally, and this records why for the banner that says so.
/// </remarks>
public sealed class UserConfigStatus
{
    /// <summary>The file every operator setting is read from and written to.</summary>
    public const string FileName = "userconfig.json";

    /// <summary>Where the file is expected, shown to whoever has to fix it.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The reason it could not be read, or null when there is nothing wrong. The
    /// provider's own message is kept verbatim because it carries the line and
    /// position, which is the whole of what the operator needs.
    /// </summary>
    public string? Error { get; private set; }

    public bool IsBroken => Error is not null;

    public void Fail(string error) => Error = error;

    /// <summary>
    /// Forgets a previous failure, so that a reload decides the current state.
    /// </summary>
    /// <remarks>
    /// Called before AniQueue reloads configuration it has just written itself (D36).
    /// Without it, a file that was malformed and has since been rewritten correctly
    /// would keep showing a banner describing a problem that no longer exists — and
    /// the person who just fixed it has no way to tell a stale warning from a live one.
    /// </remarks>
    public void Clear() => Error = null;
}

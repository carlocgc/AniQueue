namespace AniQueue.Core.Settings;

/// <summary>What happened when a save was attempted.</summary>
/// <param name="Saved">False when the file could not be written.</param>
/// <param name="Path">Where the file is, or would have been.</param>
/// <param name="Error">
/// Why it could not be written, phrased for the person who has to fix it.
/// </param>
public sealed record UserSettingsSaveResult(bool Saved, string Path, string? Error = null)
{
    public static UserSettingsSaveResult Success(string path) => new(true, path);

    public static UserSettingsSaveResult Failure(string path, string error) => new(false, path, error);
}

/// <summary>
/// Reads and writes <c>userconfig.json</c> — the one home for a setting that
/// describes something outside AniQueue.
/// </summary>
/// <remarks>
/// The file is regenerated, never edited in place: every save writes the whole thing
/// — header, per-key comment, value — from the key set the application already
/// knows, so nothing is preserved because nothing is read back.
///
/// Every setting is written out with its real value, so the file can be read at a
/// glance. <see cref="EnsureExistsAsync"/> seeds the first one from the
/// configuration already in effect, so it can never contradict what something else
/// supplied.
/// </remarks>
public interface IUserSettingsStore
{
    /// <summary>Where the file is, absolute, for whoever has to go and find it.</summary>
    string Path { get; }

    /// <summary>
    /// What is currently in effect, from every configuration source rather than from
    /// the file alone.
    /// </summary>
    /// <remarks>
    /// Reads the resolved configuration, so a value set by an environment variable
    /// is reported as the current one — which is what a page editing it has to show,
    /// and what stops a save silently reverting something the file never set.
    /// </remarks>
    UserSettings Read();

    /// <summary>
    /// Writes the whole file and makes the new values current.
    /// </summary>
    /// <remarks>
    /// Does not throw when the file cannot be written, and reports instead: a
    /// non-root container writing to a root-owned bind mount is a real deployment,
    /// and a save button that threw there would turn a settings edit into an error
    /// page rather than into an explanation.
    ///
    /// The reload is explicit rather than watched, because the watcher behind
    /// <c>reloadOnChange</c> does not fire reliably on Windows-host or network-share
    /// bind mounts. AniQueue is the writer, so it reloads the configuration itself
    /// and the value is live by the time this returns on every platform.
    /// </remarks>
    Task<UserSettingsSaveResult> SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the file if nothing is there yet. Returns true when one was written.
    /// </summary>
    /// <remarks>
    /// A settings file nobody knows exists is a poor escape hatch, and "create it
    /// yourself, in the right place, with the right key names" is worse — so a first
    /// boot leaves one naming every key it accepts. It is the same document a
    /// save writes, so there is one generator and one format rather than a template
    /// that can drift from the writer.
    ///
    /// <b>It describes what is already in effect, not the defaults.</b> This file is
    /// added last to the configuration chain, so a first boot writing defaults would
    /// set an empty AniList account over one an operator supplied through the
    /// environment — silently, on a machine where nobody had opened the file. Writing
    /// what is already true cannot override anything, because it agrees with it.
    ///
    /// <b>Never overwrites and never throws.</b> An existing file is the operator's
    /// work, including one they emptied deliberately.
    /// </remarks>
    Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default);
}

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
/// describes something outside AniQueue (D36).
/// </summary>
/// <remarks>
/// <b>The file is regenerated, never edited in place.</b> That is what removes the
/// objection D20 raised against a page writing it: comment-preserving round-tripping
/// is not something <c>System.Text.Json</c> does, and it is not needed if the
/// document is produced from the key set the application already knows. Every save
/// writes the whole file — header, per-key comment, value — so nothing is preserved
/// because nothing is read back.
///
/// <b>Every setting is written out with its real value.</b> An earlier version wrote
/// unset keys commented out, so that a default improved in a later release would
/// still reach an existing installation. That hedge was dropped: it cost a file
/// nobody could read at a glance — values buried among the comments that described
/// them — and the argument that made it load-bearing had already gone. Commenting
/// began as D20's way of stopping a shipped template overriding an operator's
/// environment variable, and D36 removed the environment as a settings channel.
///
/// What remains of that risk is handled once, at the only moment it exists:
/// <see cref="EnsureExistsAsync"/> seeds the first file from the configuration
/// already in effect, so it can never contradict what something else supplied.
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
    /// <b>Does not throw when the file cannot be written</b>, and reports instead.
    /// §9's non-root container writing to a root-owned bind mount is a real
    /// deployment, and a save button that throws there would turn a settings edit
    /// into an error page rather than into an explanation.
    ///
    /// <b>The reload is explicit rather than watched.</b> D20 records that the file
    /// watcher behind <c>reloadOnChange</c> does not fire reliably on Windows-host
    /// or network-share bind mounts. Since AniQueue is the writer, it does not have
    /// to wait to be told: it reloads the configuration itself, so the value is live
    /// by the time this returns on every platform.
    /// </remarks>
    Task<UserSettingsSaveResult> SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the file if nothing is there yet. Returns true when one was written.
    /// </summary>
    /// <remarks>
    /// A settings file nobody knows exists is a poor escape hatch, and "create it
    /// yourself, in the right place, with the right key names" is worse — so a first
    /// boot leaves one naming every key it accepts (D20). It is the same document a
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

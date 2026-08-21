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
/// <b>A value equal to its default is written commented out.</b> The file therefore
/// documents every key it accepts while setting only what somebody chose, which
/// keeps two properties that would otherwise conflict: an operator can read the file
/// to see what exists, and a default changed in a later version still reaches an
/// installation whose file was written before it.
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
    /// boot leaves one naming every key it accepts (D20). What it writes is the same
    /// document a save writes, with nothing set, so there is one generator and one
    /// format rather than a template that can drift from the writer.
    ///
    /// <b>Never overwrites and never throws.</b> An existing file is the operator's
    /// work, including one they emptied deliberately.
    /// </remarks>
    Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default);
}

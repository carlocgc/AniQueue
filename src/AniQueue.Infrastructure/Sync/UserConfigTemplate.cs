using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Sync;

/// <summary>
/// Writes a commented, entirely inert <c>userconfig.json</c> beside the database
/// the first time AniQueue starts.
///
/// D20 put the operator's settings in a file in their volume precisely so they can
/// be reached when the UI cannot be. A file nobody knows exists is a poor escape
/// hatch, and "create the file yourself, in the right place, with the right key
/// names" is a worse one — so the first boot leaves a template with every key
/// spelled out and every key commented out.
/// </summary>
public sealed class UserConfigTemplate(ILogger<UserConfigTemplate> logger)
{
    public const string FileName = "userconfig.json";

    /// <summary>
    /// The template. <b>Every setting is commented out, and that is load-bearing
    /// rather than tidiness.</b>
    /// </summary>
    /// <remarks>
    /// This file is added last to the configuration chain, so a key it sets beats
    /// the same key from <c>appsettings.json</c> or an environment variable. That
    /// ordering is deliberate — the hand-edited file is the escape hatch and should
    /// win — but it means a template shipping real values would silently override
    /// the <c>Sync__AniList__UserName</c> an operator set in their compose file, on
    /// a machine where nobody had opened this file at all.
    ///
    /// Commented out, it configures nothing until somebody chooses to uncomment a
    /// line, which is an act that carries the intent to override. The JSON
    /// configuration provider allows comments and trailing commas, so this parses
    /// as an empty object rather than being rejected.
    /// </remarks>
    /// <remarks>
    /// <b>One line per setting, written as a full path.</b> The JSON configuration
    /// provider takes a property name containing colons as the whole key, so
    /// <c>"Sync:AniList:UserName"</c> and the nested object spelling mean the same
    /// thing — and this spelling is the one that survives being edited by hand at
    /// two in the morning. Uncommenting a line out of a nested block leaves its
    /// closing braces behind, which produces a file that is not JSON, and a
    /// malformed settings file stops AniQueue from starting. That is a poor
    /// property for the thing an operator reaches for when something is already
    /// wrong.
    /// </remarks>
    private const string Template =
        """
        // AniQueue — operator settings.
        //
        // Every setting below is commented out, so this file changes nothing until you
        // edit it. Uncomment a line, set the value, and restart AniQueue.
        //
        // Restart rather than save-and-wait: the file is watched, but the watcher does
        // not fire reliably on Windows-host or network-share bind mounts, so a restart
        // is the way that always works.
        //
        // This file is read LAST, so a setting here overrides the same one in
        // appsettings.json or in an environment variable. That is deliberate — it is
        // how you change AniQueue's behaviour when its own pages cannot be reached.
        //
        // Each key is written out in full, one per line, so that uncommenting any
        // single line leaves a valid file. Comments and trailing commas are allowed.
        // The nested spelling used by appsettings.json works here too if you prefer it.
        //
        // Database:Path is deliberately absent: AniQueue finds this file by looking
        // beside the database, so a path set here could not be read until it was
        // already in use. Set that one in appsettings.json or the environment.
        {
          // false refuses every sync, however it was triggered — the switch to reach
          // for when syncing is doing something you want stopped now.
          // "Sync:Enabled": true,

          // The AniList username whose list is read. It must be public: AniQueue does
          // not sign in, and has no password for your account.
          // "Sync:AniList:UserName": "",

          // How long to wait for another writer before giving up on a locked database.
          // Worth raising only if large imports report timeouts.
          // "Database:BusyTimeoutSeconds": 30
        }

        """;

    /// <summary>
    /// Creates the template in <paramref name="directory"/> if nothing is there yet.
    /// Returns true when a file was written.
    /// </summary>
    /// <remarks>
    /// <b>Never overwrites, and never throws.</b> An existing file is the operator's
    /// work and is left exactly as it is, including one they emptied on purpose.
    /// Failure to write is logged and swallowed: §9 notes that a non-root container
    /// cannot write to a root-owned bind-mounted volume, and refusing to start over
    /// an unwritable convenience file would turn a hint into an outage.
    /// </remarks>
    public async Task<bool> EnsureExistsAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var path = Path.Combine(directory, FileName);

        try
        {
            if (File.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, Template, cancellationToken);

            logger.LogInformation(
                "Wrote a settings template to {UserConfigPath}. Every setting in it is commented "
                + "out, so it changes nothing until you edit it",
                path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(
                ex,
                "Could not write the settings template to {UserConfigPath}. AniQueue will run "
                + "normally; settings can still come from appsettings.json or the environment",
                path);

            return false;
        }
    }
}

using System.Globalization;
using AniQueue.Core.Domain;
using AniQueue.Core.Recommendations;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Jobs;
using AniQueue.Infrastructure.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AniQueue.Infrastructure.Settings;

/// <summary>
/// The one writer of <c>userconfig.json</c>.
/// </summary>
/// <remarks>
/// <b>It replaces the first-boot template rather than sitting beside it.</b> The
/// template was a fixed string with every key commented out; this generates the same
/// document from the key set, so a first boot is simply a save of nothing having been
/// set. One generator, one format — a template and a writer that could disagree about
/// the file's shape was a drift waiting to happen, and the drift would be invisible
/// until somebody's settings stopped loading.
/// </remarks>
public sealed class UserSettingsStore(
    IConfiguration configuration,
    UserConfigStatus status,
    ILogger<UserSettingsStore> logger) : IUserSettingsStore
{
    public string Path => status.Path;

    public UserSettings Read() => new()
    {
        // Read from resolved configuration rather than from the file, so a value an
        // environment variable supplied is reported as the current one. A page that
        // showed only what the file said would offer to "change" a value it cannot
        // see, and a save would then write a second answer beside the first.
        //
        // Read through the indexer and parsed here rather than through the binder's
        // GetValue<T>, which lives in a package Infrastructure does not reference and
        // would have to be approved. A handful of keys parsed explicitly is a smaller
        // thing to own than a dependency, and it is where the "unset means default"
        // rule becomes visible rather than implied.
        SyncEnabled = Bool(SyncKey(nameof(SyncOptions.Enabled)), UserSettings.Defaults.SyncEnabled),

        AniListUserName = Text(AniListKey(nameof(AniListSyncOptions.UserName))),

        SyncPrimarySource = OptionalEnum<AnimeSource>(SyncKey(nameof(SyncOptions.PrimarySource))),

        AniListEnabled = Bool(
            AniListKey(nameof(AniListSyncOptions.Enabled)),
            UserSettings.Defaults.AniListEnabled),

        TasksSchedule = EnumValue(
            $"{TaskOptions.SectionName}:{nameof(TaskOptions.Schedule)}",
            UserSettings.Defaults.TasksSchedule),

        RelationsEnabled = Bool(
            $"{TaskOptions.SectionName}:{nameof(TaskOptions.RelationsEnabled)}",
            UserSettings.Defaults.RelationsEnabled),

        AniListApplyUnattended = Bool(
            AniListKey(nameof(AniListSyncOptions.ApplyUnattended)),
            UserSettings.Defaults.AniListApplyUnattended),

        AniListConflictPolicy = EnumValue(
            AniListKey(nameof(AniListSyncOptions.ConflictPolicy)),
            UserSettings.Defaults.AniListConflictPolicy),

        AniListAbsencePolicy = EnumValue(
            AniListKey(nameof(AniListSyncOptions.AbsencePolicy)),
            UserSettings.Defaults.AniListAbsencePolicy),

        ScoringHistorySize = NumberOrAll(
            ScoringKey(nameof(ScoringOptions.HistorySize)),
            UserSettings.Defaults.ScoringHistorySize),

        ScoringCandidateLimit = Number(ScoringKey(nameof(ScoringOptions.CandidateLimit))),

        ScoringReturnTop = Number(ScoringKey(nameof(ScoringOptions.ReturnTop))),

        ScoringEndpoint = Text(ScoringKey(nameof(ScoringOptions.Endpoint))),

        ScoringModel = Text(ScoringKey(nameof(ScoringOptions.Model))),

        ScoringTimeoutSeconds = Number(
                ScoringKey(nameof(ScoringOptions.TimeoutSeconds)))
            ?? UserSettings.Defaults.ScoringTimeoutSeconds,

        ScoringUseStructuredOutput = Bool(
            ScoringKey(nameof(ScoringOptions.UseStructuredOutput)),
            UserSettings.Defaults.ScoringUseStructuredOutput),

        ScoringStaleAfterRatings = Number(
                ScoringKey(nameof(ScoringOptions.StaleAfterRatings)))
            ?? UserSettings.Defaults.ScoringStaleAfterRatings,

        ScoringEnabled = Bool(
            ScoringKey(nameof(ScoringOptions.Enabled)),
            UserSettings.Defaults.ScoringEnabled),


        ScoringBatchSize = Number(
                ScoringKey(nameof(ScoringOptions.BatchSize)))
            ?? UserSettings.Defaults.ScoringBatchSize,

        ScoringSweepMinutes = Number(
                ScoringKey(nameof(ScoringOptions.SweepMinutes)))
            ?? UserSettings.Defaults.ScoringSweepMinutes
    };

    private static string SyncKey(string key) => $"{SyncOptions.SectionName}:{key}";

    private static string AniListKey(string key) =>
        SyncKey($"{nameof(SyncOptions.AniList)}:{key}");

    private static string ScoringKey(string key) => $"{ScoringOptions.SectionName}:{key}";

    /// <summary>A configured string, treating blank as absent.</summary>
    /// <remarks>
    /// Blank and missing mean the same thing for every string this file holds — an
    /// account nobody has configured — and keeping them distinct would let an empty
    /// line in the file read as a username of zero characters.
    /// </remarks>
    private string? Text(string key) =>
        configuration[key] is { Length: > 0 } value ? value : null;

    /// <summary>A configured number, or null when unset or unreadable.</summary>
    /// <remarks>
    /// A value that will not parse is treated as absent rather than fatal, as is a
    /// file that will not parse at all: this is the
    /// file somebody edits when something is already wrong, and refusing to start
    /// over a typo in it is precisely backwards.
    /// </remarks>
    private int? Number(string key) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// A number, or null where the file says the key is deliberately empty.
    /// </summary>
    /// <remarks>
    /// The only setting that needs three states rather than two. <c>HistorySize</c> reads
    /// null as <i>all of them</i>, so "nobody has said" and "somebody said none in
    /// particular" stop meaning the same thing — and they cannot, because a first boot
    /// writes this file from the settings currently in effect, which on an empty chain is
    /// nothing at all. Without the distinction a fresh installation would write null and
    /// start by sending every rated title it has.
    ///
    /// Presence is asked of the configuration rather than of the value, because the JSON
    /// provider keeps a key whose value is null and <c>configuration[key]</c> cannot tell
    /// that from a key nobody wrote.
    ///
    /// A value that will not parse still falls back, as <see cref="Number"/> explains: it
    /// is present and it is not null, so it is a typo rather than an intention.
    /// </remarks>
    private int? NumberOrAll(string key, int? fallback)
    {
        if (Number(key) is { } number)
        {
            return number;
        }

        var stated = configuration.AsEnumerable().Any(
            pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

        return stated && configuration[key] is null ? null : fallback;
    }

    private bool Bool(string key, bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    /// <summary>A configured enum, by name and case-insensitively.</summary>
    /// <remarks>
    /// Names rather than the integers the database stored, because this file is read
    /// and edited by a person: "HoldForReview" says what it does and "0" does not.
    /// An unparseable value falls back to the default for the same reason a bad
    /// number does — this is the file somebody edits when something is already wrong.
    /// </remarks>
    private TEnum EnumValue<TEnum>(string key, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(configuration[key], ignoreCase: true, out var value) ? value : fallback;

    /// <summary>A configured enum where absent is a meaning of its own, not a default.</summary>
    private TEnum? OptionalEnum<TEnum>(string key) where TEnum : struct =>
        Enum.TryParse<TEnum>(configuration[key], ignoreCase: true, out var value) ? value : null;

    public async Task<UserSettingsSaveResult> SaveAsync(
        UserSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            await WriteAsync(UserSettingsDocument.Render(settings), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A non-root container writing to a root-owned bind mount reaches here,
            // and it is a deployment rather than a defect. The caller shows the reason
            // beside the control that failed; nothing else changes, because nothing was
            // written.
            logger.LogWarning(ex, "Could not write settings to {UserConfigPath}", Path);

            return UserSettingsSaveResult.Failure(Path, ex.Message);
        }

        Reload();

        // A file that was broken and has just been overwritten is no longer broken, and
        // one this write has somehow made unreadable now is. Either way the banner is
        // told by the reload rather than by an assumption made here.
        if (status.IsBroken)
        {
            return UserSettingsSaveResult.Failure(Path, status.Error!);
        }

        logger.LogInformation("Settings saved to {UserConfigPath}", Path);

        return UserSettingsSaveResult.Success(Path);
    }

    public async Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(Path))
            {
                return false;
            }

            // Seeded from what is currently in effect rather than from the defaults,
            // and that is what makes writing real values safe. This file is added last
            // to the configuration chain, so a first boot that wrote UserSettings.
            // Defaults would set an empty AniList account over one an operator had
            // supplied through the environment — silently, on a machine where nobody
            // had opened the file. Writing what is already true cannot override
            // anything, because it agrees with it.
            await WriteAsync(UserSettingsDocument.Render(Read()), cancellationToken);

            logger.LogInformation(
                "Wrote a settings file to {UserConfigPath} describing the settings currently in effect",
                Path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Refusing to start over an unwritable convenience file would turn a hint
            // into an outage, so this is a warning and the application continues.
            logger.LogWarning(
                ex,
                "Could not write a settings file to {UserConfigPath}. AniQueue will run "
                    + "normally; settings can still come from the environment or appsettings.json",
                Path);

            return false;
        }
    }

    /// <summary>
    /// Writes through a temporary file and a rename.
    /// </summary>
    /// <remarks>
    /// A direct write leaves a truncated file if the process stops midway, and a
    /// truncated settings file is one whose settings are all silently absent — the
    /// failure the one-key-per-line format exists to avoid. A rename is
    /// the cheapest way for the file to only ever be wholly old or wholly new.
    /// </remarks>
    private async Task WriteAsync(string content, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Beside the target rather than in the system temp directory, because a rename
        // across volumes is a copy and stops being atomic — and /data is a bind mount
        // on every deployment this is written for.
        var temporary = $"{Path}.tmp";

        await File.WriteAllTextAsync(temporary, content, cancellationToken);

        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>
    /// Makes what was just written the current configuration.
    /// </summary>
    /// <remarks>
    /// Explicit rather than waiting for the file watcher, which is
    /// unreliable on Windows-host and network-share bind mounts. AniQueue is the
    /// writer here, so it does not have to be told: reloading directly means the value
    /// is live by the time a save returns, identically on every platform.
    ///
    /// The status is cleared first so that the reload decides it. A file that fails to
    /// load raises the same <c>OnLoadException</c> the initial load does, which sets it
    /// again — so an error surviving this call is a current one rather than a memory of
    /// an older failure.
    /// </remarks>
    private void Reload()
    {
        if (configuration is not IConfigurationRoot root)
        {
            return;
        }

        status.Clear();
        root.Reload();
    }
}

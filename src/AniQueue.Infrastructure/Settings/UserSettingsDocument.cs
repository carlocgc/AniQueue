using System.Globalization;
using System.Text;
using System.Text.Json;
using AniQueue.Core.Settings;

namespace AniQueue.Infrastructure.Settings;

/// <summary>
/// Turns <see cref="UserSettings"/> into the text of <c>userconfig.json</c>.
///
/// Separated from the store that writes it so that what the file says can be
/// asserted without touching a disk, and so that the one place a key is described
/// is the same place it is serialised — a key added to the settings type with no
/// entry here fails a test rather than silently going unwritable.
/// </summary>
/// <remarks>
/// <b>One line per setting, written as a full key path.</b> The JSON configuration
/// provider reads a property name containing colons as the whole key, so
/// <c>"Sync:AniList:UserName"</c> means the same as the nested spelling — and it is
/// the spelling that survives being edited by hand at two in the morning.
/// Uncommenting a line out of a nested block leaves its closing braces behind, and a
/// file that will not parse is one whose settings are all silently absent (D20).
///
/// <b>Unset means commented, and that is load-bearing twice over.</b> A commented
/// key documents itself without configuring anything, so the file is readable as a
/// list of what exists; and a default changed in a later version reaches an
/// installation whose file predates it, rather than being pinned by a value written
/// out years earlier.
/// </remarks>
internal static class UserSettingsDocument
{
    /// <summary>One setting, as the file describes it.</summary>
    /// <param name="Key">The full configuration key path.</param>
    /// <param name="Comment">Why somebody would change it, in their terms.</param>
    /// <param name="Read">The value this setting currently holds.</param>
    /// <param name="Default">What it means when the line stays commented.</param>
    private sealed record Entry(
        string Key,
        string[] Comment,
        Func<UserSettings, object?> Read,
        Func<object?> Default);

    /// <summary>
    /// Every key the file accepts. <b>Adding a setting means adding a line here</b>,
    /// which <c>UserSettingsDocumentTests</c> enforces against the property list.
    /// </summary>
    private static readonly Entry[] Entries =
    [
        new(
            "Sync:Enabled",
            [
                "false refuses every sync, however it was triggered — Sync now, and any",
                "schedule you have set. The switch to reach for when syncing is doing",
                "something you want stopped now, and the one that works when AniQueue's",
                "own pages cannot be reached."
            ],
            s => s.SyncEnabled,
            () => UserSettings.Defaults.SyncEnabled),

        new(
            "Sync:AniList:UserName",
            [
                "The AniList username whose list is read. It must be public: AniQueue does",
                "not sign in, and has no password for your account.",
                "",
                "Editable on the Sources page as well as here.",
                "",
                "How often a source is read on its own is not here: that is a per-source",
                "setting kept in the database, so a copy of the database file carries it",
                "and a copy of this file does not."
            ],
            s => s.AniListUserName,
            () => UserSettings.Defaults.AniListUserName ?? string.Empty),

        new(
            "Scoring:HistorySize",
            [
                "How many of your scored titles travel with a ranking request, most",
                "recently finished first. More history means a ranking that fits your taste",
                "more closely, up to the point where it crowds out the backlog it is",
                "ranking. Zero sends none, and the ranking is then general rather than",
                "yours."
            ],
            s => s.ScoringHistorySize,
            () => UserSettings.Defaults.ScoringHistorySize),

        new(
            "Scoring:CandidateLimit",
            [
                "How many titles to offer for ranking at once. Leave it unset to offer",
                "everything you are planning to watch.",
                "",
                "When it is set, each request takes the titles that have gone longest",
                "without a score — so running it again works through the rest rather than",
                "asking about the same ones."
            ],
            s => s.ScoringCandidateLimit,
            () => 50),

        new(
            "Scoring:ReturnTop",
            [
                "How many rankings to ask the model to send back. Leave it unset for one",
                "per title offered.",
                "",
                "It does not narrow what the model considers — every title sent is still",
                "weighed. Worth setting when the reply is the slow part: a ranking with a",
                "sentence of reasoning per title is generated a word at a time, and a long",
                "one can run out of room halfway down the list."
            ],
            s => s.ScoringReturnTop,
            () => 50),

        new(
            "Scoring:IncludePersonalNotes",
            [
                "Whether your personal notes are sent along with a ranking request. Off,",
                "and off by default: notes are free text and may contain anything, so they",
                "travel only if you say so."
            ],
            s => s.ScoringIncludePersonalNotes,
            () => UserSettings.Defaults.ScoringIncludePersonalNotes),

        new(
            "Database:BusyTimeoutSeconds",
            [
                "How long to wait for another writer before giving up on a locked database.",
                "Worth raising only if large imports report timeouts."
            ],
            s => s.DatabaseBusyTimeoutSeconds,
            () => UserSettings.Defaults.DatabaseBusyTimeoutSeconds)
    ];

    /// <summary>The keys this document writes, for the test that guards the list.</summary>
    internal static IReadOnlyList<string> Keys => [.. Entries.Select(e => e.Key)];

    private const string Header =
        """
        // AniQueue — settings.
        //
        // AniQueue writes this file itself whenever you change a setting on one of its
        // pages, and rewrites the whole thing each time. Anything you add by hand outside
        // the lines below will not survive that, so keep your own notes elsewhere.
        //
        // You can still edit it directly, which is the point of it existing: it is how you
        // change AniQueue's behaviour when its own pages cannot be reached. Save the file
        // and restart AniQueue to be sure a hand edit has taken effect — the file is
        // watched, but the watcher does not fire reliably on Windows-host or network-share
        // bind mounts, so a restart is the way that always works.
        //
        // A commented-out line is a setting at its default. Uncomment it to change it.
        // Leaving it commented means a later version of AniQueue can improve the default
        // and you will get the improvement; writing the value out pins it forever.
        //
        // Each key is written in full, one per line, so uncommenting any single line
        // leaves a valid file. Comments and trailing commas are allowed. The nested
        // spelling used by appsettings.json works here too if you prefer it.
        //
        // Database:Path is deliberately absent: AniQueue finds this file by looking beside
        // the database, so a path set here could not be read until it was already in use.
        // Set that one in the environment or in appsettings.json.
        """;

    /// <summary>Renders the whole file for the settings given.</summary>
    public static string Render(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var text = new StringBuilder();

        text.Append(Header);
        text.AppendLine();
        text.AppendLine("{");

        for (var i = 0; i < Entries.Length; i++)
        {
            var entry = Entries[i];

            foreach (var line in entry.Comment)
            {
                text.AppendLine(line.Length == 0 ? "  //" : $"  // {line}");
            }

            var current = entry.Read(settings);
            var isSet = !Equals(current, entry.Read(UserSettings.Defaults));

            // A value that equals its default is shown as an illustration rather than
            // written: the line documents the key and configures nothing. For a
            // setting whose default is "unset", the illustration is a plausible value
            // rather than null, because "// key: null" teaches nobody the shape.
            var shown = isSet ? current : entry.Default();

            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"  {(isSet ? string.Empty : "// ")}{JsonSerializer.Serialize(entry.Key)}: {JsonSerializer.Serialize(shown)},");

            if (i < Entries.Length - 1)
            {
                text.AppendLine();
            }
        }

        text.AppendLine("}");

        return text.ToString();
    }
}

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
            ["Syncing at all. Set false to stop it when AniQueue's pages cannot be reached."],
            s => s.SyncEnabled,
            () => UserSettings.Defaults.SyncEnabled),

        new(
            "Sync:AniList:UserName",
            [
                "Whose AniList list to read. Must be public — AniQueue does not sign in.",
                "Also editable on the Sources page."
            ],
            s => s.AniListUserName,
            () => UserSettings.Defaults.AniListUserName ?? string.Empty),

        new(
            "Scoring:HistorySize",
            ["Your scored titles sent with a ranking request, newest first. 0 sends none."],
            s => s.ScoringHistorySize,
            () => UserSettings.Defaults.ScoringHistorySize),

        new(
            "Scoring:CandidateLimit",
            [
                "Titles offered per ranking request. Unset offers all of them.",
                "When set, takes those longest without a score, so repeats sweep the rest."
            ],
            s => s.ScoringCandidateLimit,
            () => 50),

        new(
            "Scoring:ReturnTop",
            [
                "Rankings asked for back. Unset asks for one per title offered.",
                "Every title sent is still weighed; this only shortens the reply."
            ],
            s => s.ScoringReturnTop,
            () => 50)
    ];

    /// <summary>The keys this document writes, for the test that guards the list.</summary>
    internal static IReadOnlyList<string> Keys => [.. Entries.Select(e => e.Key)];

    /// <summary>
    /// The preamble, kept short on purpose.
    /// </summary>
    /// <remarks>
    /// This file is read by somebody who is already having a problem, so it is scanned
    /// rather than studied. Every line that explains something they did not ask about
    /// pushes the line they need further down — which is why the guidance here is the
    /// four facts that change what they do, and the per-key comments below are one
    /// line each wherever one line will carry it.
    /// </remarks>
    private const string Header =
        """
        // AniQueue settings.
        //
        // AniQueue rewrites this file whole when you change something in the app, so
        // notes of your own will not survive. Editing it by hand works, and is how you
        // change AniQueue's behaviour when its pages cannot be reached — restart
        // afterwards to be sure a hand edit took.
        //
        // A commented-out line is that setting at its default. Uncomment to change it;
        // leaving it commented lets a later version improve the default.
        //
        // Comments and trailing commas are fine. Database settings are not here — set
        // Database__Path or Database__BusyTimeoutSeconds in the environment.
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

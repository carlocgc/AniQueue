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
/// the spelling that survives being edited by hand at two in the morning. A nested
/// block edited badly leaves stray braces, and a file that will not parse is one
/// whose settings are all silently absent (D20).
///
/// <b>Every setting is written out, and each carries one line saying what it does.</b>
/// The file is what somebody opens when something is already wrong, so it has to be
/// readable in one pass: the values are the content, and the comments are captions
/// rather than documentation. Anything longer belongs in the README.
/// </remarks>
internal static class UserSettingsDocument
{
    /// <summary>One setting, as the file describes it.</summary>
    /// <param name="Key">The full configuration key path.</param>
    /// <param name="Comment">What it does, in as few words as will carry it.</param>
    /// <param name="Read">The value to write.</param>
    private sealed record Entry(string Key, string[] Comment, Func<UserSettings, object?> Read);

    /// <summary>
    /// Every key the file accepts. <b>Adding a setting means adding a line here</b>,
    /// which <c>UserSettingsDocumentTests</c> enforces against the property list.
    /// </summary>
    private static readonly Entry[] Entries =
    [
        new(
            "Sync:Enabled",
            ["Sync at all. false stops every sync, including any you have scheduled."],
            s => s.SyncEnabled),

        new(
            "Sync:AniList:UserName",
            ["Whose AniList list to read. Must be public — AniQueue does not sign in."],
            // Empty rather than null when unset, because this is the line a new
            // installation is most likely to edit by hand: replacing "" with a name is
            // obvious, while replacing null means also knowing to add the quotes. The
            // reader treats blank and absent alike, so nothing changes meaning.
            s => s.AniListUserName ?? string.Empty),

        new(
            "Scoring:HistorySize",
            ["Your scored titles sent with a ranking request, newest first. 0 sends none."],
            s => s.ScoringHistorySize),

        new(
            "Scoring:CandidateLimit",
            [
                "Titles offered per ranking request. null offers all of them.",
                "A number takes those longest without a score, so repeats sweep the rest."
            ],
            s => s.ScoringCandidateLimit),

        new(
            "Scoring:ReturnTop",
            [
                "Rankings asked for back. null asks for one per title offered.",
                "Every title sent is still weighed; this only shortens the reply."
            ],
            s => s.ScoringReturnTop),

        new(
            "Scoring:Endpoint",
            [
                "Where your model is listening, e.g. http://192.168.1.50:1234 for LM Studio",
                "or http://localhost:11434 for Ollama. Empty means you rank by hand instead."
            ],
            s => s.ScoringEndpoint ?? string.Empty),

        new(
            "Scoring:Model",
            ["Which model to ask for. Whatever your server calls it."],
            s => s.ScoringModel ?? string.Empty),

        new(
            "Scoring:TimeoutSeconds",
            ["How long to wait for a ranking. Ranking a large backlog is slow."],
            s => s.ScoringTimeoutSeconds),

        new(
            "Scoring:UseStructuredOutput",
            [
                "Ask the server to reply in JSON and nothing else. Leave this on unless",
                "your server rejects the request; AniQueue copes either way."
            ],
            s => s.ScoringUseStructuredOutput)
    ];

    /// <summary>The keys this document writes, for the test that guards the list.</summary>
    internal static IReadOnlyList<string> Keys => [.. Entries.Select(e => e.Key)];

    /// <summary>
    /// The only line of preamble, and it earns its place by preventing data loss.
    /// </summary>
    /// <remarks>
    /// Everything else that was here has gone. Explaining the format, the reload
    /// semantics, and which settings deliberately live elsewhere is documentation, and
    /// documentation at the top of a settings file is read once and then scrolled past
    /// forever — while pushing the thing somebody actually came for further down.
    ///
    /// This line stays because it is not documentation but a warning about a surprise:
    /// AniQueue regenerates the whole file, so a note somebody adds here disappears at
    /// the next save with nothing to explain why.
    /// </remarks>
    private const string Header =
        "// AniQueue rewrites this file whenever a setting changes, so notes you add here will not survive.";

    /// <summary>Renders the whole file for the settings given.</summary>
    public static string Render(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var text = new StringBuilder();

        text.AppendLine(Header);
        text.AppendLine("{");

        for (var i = 0; i < Entries.Length; i++)
        {
            var entry = Entries[i];

            foreach (var line in entry.Comment)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  // {line}");
            }

            // No trailing comma on the last one. The provider permits it, but this file
            // is meant to be read, and a stray comma is the kind of thing that makes a
            // reader wonder whether something is missing below it.
            var separator = i < Entries.Length - 1 ? "," : string.Empty;

            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"  {JsonSerializer.Serialize(entry.Key)}: {JsonSerializer.Serialize(entry.Read(settings))}{separator}");

            if (i < Entries.Length - 1)
            {
                text.AppendLine();
            }
        }

        text.AppendLine("}");

        return text.ToString();
    }
}

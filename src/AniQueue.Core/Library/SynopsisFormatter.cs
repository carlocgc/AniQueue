using System.Text;

namespace AniQueue.Core.Library;

/// <summary>
/// One run of a synopsis, and whether it gives something away.
/// </summary>
/// <param name="Text">
/// Plain text. Line breaks are <c>\n</c>; there is no markup left in here, which is
/// what lets a page render it as text and never as HTML.
/// </param>
/// <param name="IsSpoiler">Whether the page should mask this until asked.</param>
public readonly record struct SynopsisSegment(string Text, bool IsSpoiler);

/// <summary>
/// Turns AniList's synopsis markdown into runs a page can render as text.
/// </summary>
/// <remarks>
/// Nothing here escapes anything, because nothing downstream renders HTML. The
/// output is text, Blazor encodes it, and <c>MarkupString</c> is never involved — so
/// this is a formatter rather than a sanitiser, and dropping a tag it does not
/// recognise is a cosmetic decision rather than a security one. That is the whole
/// reason the synopsis is stored as AniList markdown rather than as its
/// <c>asHtml</c> form: the dangerous version was never taken in.
///
/// What it actually has to handle was measured, not guessed. Across the 810
/// synopses this library stores: <c>&lt;br&gt;</c> in 1,538 places, <c>&lt;i&gt;</c>
/// in 213, <c>&lt;b&gt;</c> in 44, <c>&lt;strong&gt;</c> in 12, and no anchors at all.
/// AniList's users write HTML into a markdown field, so a formatter that only knew
/// about line breaks would print a literal <c>&lt;i&gt;</c> to the reader in about a
/// fifth of the library.
///
/// <b>The spoiler rule is the one thing here that is unproven against real data.</b>
/// AniList wraps spoilers in <c>~!...!~</c> and this library contains none, so the
/// behaviour below rests on tests rather than on a measurement. It is built anyway
/// because a dialog whose purpose is interesting somebody in an unwatched show is
/// exactly the wrong place to discover the convention works differently.
/// </remarks>
public static class SynopsisFormatter
{
    /// <summary>
    /// Splits a stored synopsis into runs, masking what AniList marked as a spoiler.
    /// </summary>
    /// <remarks>
    /// An empty or absent synopsis yields no segments, and the dialog then shows no
    /// synopsis at all rather than an empty panel.
    /// </remarks>
    public static IReadOnlyList<SynopsisSegment> Parse(string? synopsis)
    {
        if (string.IsNullOrWhiteSpace(synopsis))
        {
            return [];
        }

        var segments = new List<SynopsisSegment>();
        var buffer = new StringBuilder(synopsis.Length);
        var spoiler = false;
        var index = 0;

        void Flush(bool isSpoiler)
        {
            var text = Tidy(buffer.ToString());
            buffer.Clear();

            if (text.Length > 0)
            {
                segments.Add(new SynopsisSegment(text, isSpoiler));
            }
        }

        while (index < synopsis.Length)
        {
            var character = synopsis[index];

            if (!spoiler && character == '~' && Next(synopsis, index) == '!')
            {
                Flush(isSpoiler: false);
                spoiler = true;
                index += 2;
                continue;
            }

            if (spoiler && character == '!' && Next(synopsis, index) == '~')
            {
                Flush(isSpoiler: true);
                spoiler = false;
                index += 2;
                continue;
            }

            if (character == '<' && TryReadTag(synopsis, index, out var name, out var after))
            {
                // A line break is the only tag whose meaning survives into plain text.
                // Every other one is dropped and its content kept, because the text
                // between <i> and </i> is still the sentence the reader wants.
                if (name is "br")
                {
                    buffer.Append('\n');
                }

                index = after;
                continue;
            }

            buffer.Append(character);
            index++;
        }

        // An unterminated ~! masks everything after it rather than nothing. Both
        // readings are defensible for malformed input and only one of them can print
        // a twist to somebody who did not ask for it.
        Flush(spoiler);

        return segments;
    }

    private static char Next(string text, int index) =>
        index + 1 < text.Length ? text[index + 1] : '\0';

    /// <summary>
    /// Reads a tag at <paramref name="index"/>, if what is there is one.
    /// </summary>
    /// <remarks>
    /// Deliberately strict about what counts: a <c>&lt;</c> has to be followed by a
    /// letter or a slash, and there has to be a closing <c>&gt;</c>. A synopsis
    /// saying "a &lt; b" keeps its angle bracket, which a looser reading would eat.
    /// </remarks>
    private static bool TryReadTag(string text, int index, out string name, out int after)
    {
        name = string.Empty;
        after = index;

        var start = index + 1;
        if (start >= text.Length)
        {
            return false;
        }

        if (text[start] == '/')
        {
            start++;
        }

        if (start >= text.Length || !char.IsAsciiLetter(text[start]))
        {
            return false;
        }

        var close = text.IndexOf('>', start);
        if (close < 0)
        {
            return false;
        }

        var end = start;
        while (end < close && char.IsAsciiLetterOrDigit(text[end]))
        {
            end++;
        }

        name = text[start..end].ToLowerInvariant();
        after = close + 1;
        return true;
    }

    /// <summary>
    /// Trims a run and collapses the blank space AniList's line breaks leave behind.
    /// </summary>
    /// <remarks>
    /// Synopses routinely end a paragraph with two or three <c>&lt;br&gt;</c> in a
    /// row, which would otherwise open a hole in the middle of the dialog. Two
    /// newlines survive, because that is a paragraph break and worth keeping.
    /// </remarks>
    private static string Tidy(string text)
    {
        var collapsed = new StringBuilder(text.Length);
        var newlines = 0;

        foreach (var character in text)
        {
            if (character == '\n')
            {
                newlines++;

                if (newlines <= 2)
                {
                    collapsed.Append(character);
                }

                continue;
            }

            newlines = 0;
            collapsed.Append(character);
        }

        return collapsed.ToString().Trim();
    }
}

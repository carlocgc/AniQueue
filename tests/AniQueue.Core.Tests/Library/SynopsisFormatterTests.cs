using AniQueue.Core.Library;

namespace AniQueue.Core.Tests.Library;

/// <summary>
/// What a stored synopsis becomes on the way to the detail dialog (D49).
/// </summary>
/// <remarks>
/// The spoiler cases carry more weight than usual: the development library contains
/// no <c>~!...!~</c> at all, so unlike the tag handling below — which was written
/// against measured counts from 810 real synopses — this behaviour has never been
/// exercised by real data and these tests are the only thing holding it up.
/// </remarks>
public class SynopsisFormatterTests
{
    private static string TextOf(IReadOnlyList<SynopsisSegment> segments) =>
        string.Concat(segments.Select(s => s.Text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_title_with_no_synopsis_produces_nothing_to_render(string? synopsis) =>
        Assert.Empty(SynopsisFormatter.Parse(synopsis));

    [Fact]
    public void A_line_break_becomes_a_line_break_rather_than_the_word_br()
    {
        // 1,538 of these across the development library. Rendering the tag literally
        // is what taking the markdown form rather than asHtml costs, and paying it
        // here is the whole reason the raw form is safe to store.
        var segments = SynopsisFormatter.Parse("First line.<br>Second line.");

        var segment = Assert.Single(segments);
        Assert.Equal("First line.\nSecond line.", segment.Text);
        Assert.False(segment.IsSpoiler);
    }

    [Theory]
    [InlineData("<br>")]
    [InlineData("<br/>")]
    [InlineData("<br />")]
    [InlineData("<BR>")]
    public void Every_spelling_of_a_line_break_is_recognised(string tag) =>
        Assert.Equal("a\nb", Assert.Single(SynopsisFormatter.Parse($"a{tag}b")).Text);

    [Fact]
    public void Inline_formatting_is_dropped_and_the_words_inside_it_are_kept()
    {
        // <i> appears in 213 of this library's synopses and <b> in 44. Encoding them
        // as text would print the tag to the reader; dropping the whole element would
        // lose the sentence.
        var segments = SynopsisFormatter.Parse("The <i>Mobile Suit</i> was <b>lost</b>.");

        Assert.Equal("The Mobile Suit was lost.", Assert.Single(segments).Text);
    }

    [Fact]
    public void A_less_than_sign_that_is_not_a_tag_survives()
    {
        // "a < b" is prose, not markup, and a looser reading would eat the rest of
        // the sentence looking for a closing bracket.
        Assert.Equal("Power levels of a < b are rare.",
            Assert.Single(SynopsisFormatter.Parse("Power levels of a < b are rare.")).Text);
    }

    [Fact]
    public void An_unclosed_bracket_is_left_alone_rather_than_swallowing_the_rest()
    {
        Assert.Equal("It costs < 500 yen", Assert.Single(SynopsisFormatter.Parse("It costs < 500 yen")).Text);
    }

    [Fact]
    public void A_spoiler_is_split_out_so_the_page_can_hide_it()
    {
        var segments = SynopsisFormatter.Parse("Humanity fights back. ~!Eren is the villain.!~ A classic.");

        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].IsSpoiler);
        Assert.Equal("Humanity fights back.", segments[0].Text);

        Assert.True(segments[1].IsSpoiler);
        Assert.Equal("Eren is the villain.", segments[1].Text);

        Assert.False(segments[2].IsSpoiler);
        Assert.Equal("A classic.", segments[2].Text);
    }

    [Fact]
    public void More_than_one_spoiler_in_a_synopsis_is_masked_separately()
    {
        var segments = SynopsisFormatter.Parse("a ~!one!~ b ~!two!~ c");

        Assert.Equal(["one", "two"], segments.Where(s => s.IsSpoiler).Select(s => s.Text));
        Assert.Equal(["a", "b", "c"], segments.Where(s => !s.IsSpoiler).Select(s => s.Text));
    }

    [Fact]
    public void An_unterminated_spoiler_masks_the_rest_rather_than_none_of_it()
    {
        // Both readings are defensible for malformed input, and only one of them can
        // print a twist to a reader who did not ask for it.
        var segments = SynopsisFormatter.Parse("Safe. ~!The rest is not.");

        Assert.Equal(2, segments.Count);
        Assert.False(segments[0].IsSpoiler);
        Assert.True(segments[1].IsSpoiler);
        Assert.Equal("The rest is not.", segments[1].Text);
    }

    [Fact]
    public void A_spoiler_carrying_markup_is_still_cleaned_up()
    {
        var segments = SynopsisFormatter.Parse("~!He <i>dies</i>.<br>Twice.!~");

        var segment = Assert.Single(segments);
        Assert.True(segment.IsSpoiler);
        Assert.Equal("He dies.\nTwice.", segment.Text);
    }

    [Fact]
    public void A_run_of_line_breaks_does_not_open_a_hole_in_the_dialog()
    {
        // Synopses routinely end a paragraph with three of these in a row. Two
        // survive, because that is a paragraph break and worth keeping.
        Assert.Equal("One.\n\nTwo.", Assert.Single(SynopsisFormatter.Parse("One.<br><br><br><br>Two.")).Text);
    }

    [Fact]
    public void Nothing_that_looks_like_markup_survives_into_the_output()
    {
        // The page renders these as text, so this is about not showing the reader
        // angle brackets rather than about safety — but a formatter that let a tag
        // through would be the first sign that assumption had stopped holding.
        const string Messy = "<p>Alpha</p><br><strong>Beta</strong><em>Gamma</em>";

        var text = TextOf(SynopsisFormatter.Parse(Messy));

        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.DoesNotContain(">", text, StringComparison.Ordinal);
        Assert.Contains("Alpha", text, StringComparison.Ordinal);
        Assert.Contains("Gamma", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_synopsis_from_the_library_comes_through_intact()
    {
        // Copied from the development database rather than invented, so the shape
        // being asserted is the shape AniList actually publishes.
        const string Real =
            "Determined to achieve his own mysterious ends, Shin, the captain of Spearhead Squadron, "
            + "which is comprised of Eighty-sixers, continues to fight a hopeless war on a battlefield "
            + "where only death awaits him.<br>";

        var segment = Assert.Single(SynopsisFormatter.Parse(Real));

        Assert.EndsWith("only death awaits him.", segment.Text, StringComparison.Ordinal);
        Assert.False(segment.IsSpoiler);
    }
}

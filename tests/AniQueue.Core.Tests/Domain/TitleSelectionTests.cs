using AniQueue.Core.Domain;

namespace AniQueue.Core.Tests.Domain;

/// <summary>
/// The fallback chain, which decides which of a title's names a reader sees (D22).
///
/// Worth its own suite because two things must agree with it: the import resolving
/// a title as it writes a row, and the SQL that recomputes every row when the
/// preference changes. A disagreement between them shows up as different languages
/// on different pages.
/// </summary>
public class TitleSelectionTests
{
    private const string Romaji = "Shingeki no Kyojin";
    private const string English = "Attack on Titan";
    private const string Native = "進撃の巨人";

    [Theory]
    [InlineData(TitleLanguage.Romaji, Romaji)]
    [InlineData(TitleLanguage.English, English)]
    [InlineData(TitleLanguage.Native, Native)]
    public void The_preferred_language_wins_when_it_exists(TitleLanguage preferred, string expected) =>
        Assert.Equal(expected, TitleSelection.Resolve(preferred, Romaji, English, Native, "fallback"));

    [Fact]
    public void A_missing_english_falls_back_rather_than_leaving_nothing()
    {
        // English is absent for roughly one title in seven, so this is the ordinary
        // case rather than the edge one.
        Assert.Equal(Romaji, TitleSelection.Resolve(TitleLanguage.English, Romaji, null, Native, "fallback"));
    }

    [Fact]
    public void The_order_after_the_preference_is_fixed()
    {
        // Not "whatever is present": the same row must resolve the same way every
        // time, however many variants a particular fetch happened to include.
        Assert.Equal(Romaji, TitleSelection.Resolve(TitleLanguage.Native, Romaji, English, null, "fallback"));
        Assert.Equal(English, TitleSelection.Resolve(TitleLanguage.Native, null, English, null, "fallback"));
    }

    [Fact]
    public void A_source_with_one_name_keeps_it()
    {
        // Every manual entry and every MyAnimeList import is in this position
        // permanently, whatever the preference says.
        Assert.Equal(
            "Only Name",
            TitleSelection.Resolve(TitleLanguage.English, null, null, null, "Only Name"));
    }

    [Fact]
    public void A_blank_variant_counts_as_missing()
    {
        // A source returning an empty string is not offering a title, and writing
        // one into a required column would leave a row with no readable name.
        Assert.Equal(Romaji, TitleSelection.Resolve(TitleLanguage.English, Romaji, "   ", Native, "fallback"));
    }
}

namespace AniQueue.Core.Domain;

/// <summary>
/// Chooses which title variant to display, and falls back when the preferred one
/// does not exist (D22).
/// </summary>
/// <remarks>
/// One implementation, because there are three callers and they must agree: the
/// import resolving a title as it writes a row, the settings change recomputing
/// every row, and any future surface that wants to show an alternative. Two
/// implementations of a fallback chain is how a library ends up displaying
/// different languages on different pages.
///
/// The fallback is not defensive tidiness. English is absent for roughly one title
/// in seven, so a preference for it without a chain behind it would leave a seventh
/// of the library with no name at all.
/// </remarks>
public static class TitleSelection
{
    /// <summary>
    /// The preferred variant if it exists, then the others in a fixed order, then
    /// <paramref name="fallback"/> — which is what a source publishing a single
    /// title supplies, and is never null.
    /// </summary>
    public static string Resolve(
        TitleLanguage preferred,
        string? romaji,
        string? english,
        string? native,
        string fallback)
    {
        // The order after the preference is fixed rather than "whatever is
        // present", so the same row resolves the same way every time regardless of
        // which variants a particular fetch happened to include.
        var chain = preferred switch
        {
            TitleLanguage.English => new[] { english, romaji, native },
            TitleLanguage.Native => new[] { native, romaji, english },
            _ => new[] { romaji, english, native }
        };

        return chain.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? fallback;
    }
}

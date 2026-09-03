namespace AniQueue.Core.Domain;

/// <summary>
/// Chooses which title variant to display, and falls back when the preferred one
/// does not exist. One implementation, because the import, the settings change and
/// any surface showing an alternative must all resolve a title the same way.
/// </summary>
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
        // Fixed rather than "whatever is present", so the same row resolves the
        // same way regardless of which variants a particular fetch included.
        var chain = preferred switch
        {
            TitleLanguage.English => new[] { english, romaji, native },
            TitleLanguage.Native => new[] { native, romaji, english },
            _ => new[] { romaji, english, native }
        };

        return chain.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? fallback;
    }
}

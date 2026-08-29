namespace AniQueue.Core.Domain;

/// <summary>
/// Which title variant the user wants to read. AniList publishes three and a
/// MyAnimeList export one, so this is the choice that decides what a sync writes
/// to <see cref="Anime.Title"/>.
///
/// AniList's <c>userPreferred</c> is not offered: it resolves against the AniList
/// account's own display setting, which the user cannot see from here.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum TitleLanguage
{
    /// <summary>The romanised original. Closest to what a MyAnimeList export already holds.</summary>
    Romaji = 0,

    /// <summary>The official English title, where one exists — absent for roughly one title in seven.</summary>
    English = 1,

    /// <summary>The title in its original script.</summary>
    Native = 2
}

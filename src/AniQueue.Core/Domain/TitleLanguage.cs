namespace AniQueue.Core.Domain;

/// <summary>
/// Which title variant the user wants to read (D22).
///
/// A MyAnimeList export publishes one title, roughly romaji; AniList publishes
/// three, so the first sync would otherwise rewrite the displayed name of most of
/// the library — <i>Shingeki no Kyojin</i> becoming <i>Attack on Titan</i> across
/// every row and queue slot — driven by a choice nobody made. This is
/// that choice, made explicitly.
///
/// <c>userPreferred</c> is deliberately not offered: it resolves against the
/// AniList account's own display setting, which would make a committed test
/// fixture irreproducible and the application's behaviour depend on a value the
/// user cannot see from here.
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

namespace AniQueue.Core.Domain;

/// <summary>
/// A service that catalogues anime. On <see cref="Anime.Source"/> this records
/// provenance only — how the record came to exist here. Identity is
/// <see cref="AnimeExternalId"/>, which a title may carry several of.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum AnimeSource
{
    /// <summary>Created by hand in the application; has no external identifier.</summary>
    Manual = 0,
    MyAnimeList = 1,
    AniList = 2
}

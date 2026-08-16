namespace AniQueue.Core.Domain;

/// <summary>
/// Where a title's metadata originated. Combined with
/// <see cref="Anime.SourceAnimeId"/> this is the primary key for import
/// deduplication (ROADMAP.md §6).
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum AnimeSource
{
    /// <summary>Created by hand in the application; has no external identifier.</summary>
    Manual = 0,
    MyAnimeList = 1,

    /// <summary>
    /// Reserved. No AniList integration exists in the MVP and none is faked;
    /// the value exists so imported data can be labelled correctly when the
    /// provider is added.
    /// </summary>
    AniList = 2
}

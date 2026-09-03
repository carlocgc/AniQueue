namespace AniQueue.Core.Domain;

/// <summary>
/// One genre, once, however many titles carry it. A table rather than an enum so
/// that a genre AniList adds mid-sync does not break a data contract, and
/// normalised rather than delimited so filtering can be indexed and server-side.
/// </summary>
public class Genre
{
    public int Id { get; set; }

    /// <summary>
    /// The genre as AniList names it, stored verbatim — not case-normalised, not
    /// mapped onto a local vocabulary. AniList is the only source that publishes
    /// these, so there is no second spelling to reconcile with.
    /// </summary>
    public required string Name { get; set; }

    public ICollection<AnimeGenre> Anime { get; set; } = [];
}

/// <summary>
/// A title carries a genre. A pure join, unlike <see cref="AnimeStudio"/>, because
/// AniList publishes genres as a flat list of strings and studios as edges with a
/// flag on them.
/// </summary>
public class AnimeGenre
{
    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public int GenreId { get; set; }

    public Genre? Genre { get; set; }
}

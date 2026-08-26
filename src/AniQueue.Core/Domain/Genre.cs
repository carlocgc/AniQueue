namespace AniQueue.Core.Domain;

/// <summary>
/// One genre, once, however many titles carry it (D49).
/// </summary>
/// <remarks>
/// <b>A table rather than an enum</b>, because AniList can add a genre and an enum
/// member is a data contract that cannot absorb one arriving mid-sync. The vocabulary
/// is small and closed in practice, which is why this costs almost nothing.
///
/// <b>Normalised rather than a delimited column</b> because §6 requires filtering to
/// be indexed and server-side, and <c>LIKE '%Shonen%'</c> is neither. Nothing filters
/// on genre in Phase 9b or 9c — the dialog renders chips — so this is built ahead of
/// its consumer, which D11 would normally argue against. It is still the cheaper
/// order: one migration either way, both squashed into Phase 11's baseline, and the
/// delimited form would owe a data migration the day a filter arrives.
/// </remarks>
public class Genre
{
    public int Id { get; set; }

    /// <summary>
    /// The genre as AniList names it, stored verbatim.
    /// </summary>
    /// <remarks>
    /// Not normalised in case, not mapped onto a local vocabulary. AniList is the only
    /// source that publishes these, so there is no second spelling to reconcile with,
    /// and inventing a canonical form would mean deciding what to do the first time
    /// they add one this application has never heard of.
    /// </remarks>
    public required string Name { get; set; }

    public ICollection<AnimeGenre> Anime { get; set; } = [];
}

/// <summary>
/// A title carries a genre. Nothing else is true about the pairing.
/// </summary>
/// <remarks>
/// A pure join, unlike <see cref="AnimeStudio"/>, which has to carry <c>IsMain</c>.
/// The asymmetry is deliberate and worth not tidying away: AniList publishes genres as
/// a flat list of strings and studios as edges with a flag on them.
/// </remarks>
public class AnimeGenre
{
    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public int GenreId { get; set; }

    public Genre? Genre { get; set; }
}

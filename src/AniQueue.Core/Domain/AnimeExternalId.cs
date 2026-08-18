namespace AniQueue.Core.Domain;

/// <summary>
/// One title's identifier on one external service (D17).
///
/// A title carries zero or more of these, which is the whole point: an AniList
/// sync publishes <c>Media.idMal</c> alongside its own id, so storing both leaves
/// a MyAnimeList identifier waiting for an export that lands later — and a
/// MyAnimeList-imported library is matched rather than duplicated when AniList
/// arrives. Under the old single-identifier model neither direction worked, and
/// every title in a real library conflicted on first sync.
///
/// <see cref="Anime.Source"/> still exists and still means something, but only
/// provenance: how the record came to be here. Identity is this table.
/// </summary>
public class AnimeExternalId
{
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    /// <summary>
    /// The service that issued <see cref="ExternalId"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AnimeSource.Manual"/> never appears here. A hand-created title
    /// has no external identifier and therefore no rows at all, which is what lets
    /// the uniqueness index below be unfiltered.
    /// </remarks>
    public AnimeSource Source { get; set; }

    /// <summary>
    /// The identifier as the source states it, kept as text.
    /// </summary>
    /// <remarks>
    /// Not an integer, even though both services currently issue numbers: these
    /// values arrive from imported files and are not trusted to be numeric.
    /// Anything that needs a number parses it at the point of use, as
    /// <see cref="Library.SourceLinkBuilder"/> does before putting one in a URL.
    /// </remarks>
    public required string ExternalId { get; set; }

    /// <summary>
    /// When a structurally complete fetch from <see cref="Source"/> last came back
    /// without this title, or null while the source is still listing it (D19).
    /// </summary>
    /// <remarks>
    /// Absence is a fact about a title <i>on one service</i>, which is exactly what
    /// this row already is, so it is recorded here rather than on the library entry
    /// — a title dropped from AniList while still on MyAnimeList is absent from one
    /// and present on the other, and one flag on the entry could not say that.
    ///
    /// It is written by the flag policy and cleared the moment the source lists the
    /// title again, so it always describes the most recent fetch rather than
    /// accumulating history. Only rows that have one of these are ever in scope for
    /// absence at all, which is the structural protection D19 depends on: a
    /// MyAnimeList-only title has no AniList row here, so no AniList policy can
    /// reach it whatever the user sets.
    ///
    /// It is also the exact population Phase 8's <see cref="SyncAbsencePolicy.Remove"/>
    /// will act on, once a backup exists to make removal recoverable.
    /// </remarks>
    public DateTimeOffset? MissingFromSourceAt { get; set; }
}

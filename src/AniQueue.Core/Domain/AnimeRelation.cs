namespace AniQueue.Core.Domain;

/// <summary>
/// One edge in the relation graph, as one source published it (D24).
///
/// Keyed by **external identifiers rather than `AnimeId` pairs**, and that is the
/// decision this table turns on. Relations routinely point at titles the user does
/// not own — an unwatched middle season, a spin-off never planned — and resolving
/// at write time would discard exactly those edges, permanently, on the strength of
/// what happened to be in the library that afternoon. Identifiers survive titles
/// arriving in any order, and resolve through <see cref="AnimeExternalId"/> with a
/// join when something needs to be displayed.
///
/// **Stored exactly as fetched, never normalised into one direction.** AniList
/// states an edge from the perspective of the media that was queried, so which end
/// spoke is part of the fact. Reading from the far end inverts the type instead —
/// see <see cref="RelationTypes.Invert"/>.
/// </summary>
public class AnimeRelation
{
    public int Id { get; set; }

    /// <summary>
    /// The service that published this edge, which is also the service whose
    /// identifiers both ends are written in.
    /// </summary>
    /// <remarks>
    /// <see cref="AnimeSource.Manual"/> never appears here for the reason it never
    /// appears on an identifier row: nothing local publishes relationships, and
    /// after D23 nothing local ever will.
    /// </remarks>
    public AnimeSource Source { get; set; }

    /// <summary>The title the edge was fetched for, as <see cref="Source"/> identifies it.</summary>
    public required string ExternalId { get; set; }

    public RelationType RelationType { get; set; }

    /// <summary>
    /// The title at the other end. May be one the user does not own, and often is.
    /// </summary>
    public required string RelatedExternalId { get; set; }
}

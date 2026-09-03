namespace AniQueue.Core.Domain;

/// <summary>
/// One title's identifier on one external service. A title carries zero or more
/// of these, so a library imported from one service is matched rather than
/// duplicated when another one syncs.
/// </summary>
public class AnimeExternalId
{
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    /// <summary>
    /// The service that issued <see cref="ExternalId"/>.
    /// <see cref="AnimeSource.Manual"/> never appears: a hand-created title has no
    /// external identifier and therefore no row here.
    /// </summary>
    public AnimeSource Source { get; set; }

    /// <summary>
    /// The identifier as the source states it, kept as text because it arrives
    /// from imported files and is not trusted to be numeric. Callers that need a
    /// number parse it at the point of use.
    /// </summary>
    public required string ExternalId { get; set; }

    /// <summary>
    /// When a structurally complete fetch from <see cref="Source"/> last came back
    /// without this title, or null while the source is still listing it. Cleared
    /// the moment the source lists the title again, so it always describes the most
    /// recent fetch.
    /// </summary>
    public DateTimeOffset? MissingFromSourceAt { get; set; }

    /// <summary>
    /// When the user answered that absence by choosing to keep the title, or null
    /// while it is still waiting on an answer. Cleared alongside
    /// <see cref="MissingFromSourceAt"/> the moment the source lists the title
    /// again, so a title that leaves twice is asked about twice.
    /// </summary>
    /// <remarks>
    /// A second column rather than clearing the mark, because the mark records what
    /// the fetch observed and the next fetch would write it straight back. Missing
    /// and unanswered is the only state that needs the user.
    /// </remarks>
    public DateTimeOffset? AbsenceKeptAt { get; set; }

    /// <summary>
    /// When this title's relations were last asked for from <see cref="Source"/>,
    /// or null while they have never been fetched. It records that the question was
    /// asked, not that edges came back, so a title with no relations is not
    /// re-queued forever. Expires after thirty days.
    /// </summary>
    /// <remarks>
    /// A <c>DateTime</c> rather than a <c>DateTimeOffset</c> because SQLite cannot
    /// translate a comparison on the latter, and this is the only timestamp in the
    /// model that a <c>WHERE</c> has to compare. Always written as UTC.
    /// </remarks>
    public DateTime? RelationsFetchedAt { get; set; }
}

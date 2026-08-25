namespace AniQueue.Core.Domain;

/// <summary>
/// What an image of a title actually shows.
///
/// Stored as an integer; values are a database contract. Append only — reordering
/// or removing one is a data break, not a rename.
/// </summary>
/// <remarks>
/// Phase 9a writes only <see cref="Poster"/>, because AniList publishes one cover
/// per title and that is the whole of what it has. The rest exist because D25's
/// second schema warning is the reason this enum exists at all: poster, banner,
/// logo and backdrop are a set rather than a field, and a table keyed by kind is
/// what stops the arity-1 mistake D17 spent Phase 5a undoing for identity being
/// repeated for art. Phase 9b fills them in from TVDB and TMDB.
/// </remarks>
public enum ImageKind
{
    Poster = 0,
    Banner = 1,
    ClearLogo = 2,
    Backdrop = 3
}

namespace AniQueue.Core.Domain;

/// <summary>
/// What an image of a title actually shows.
///
/// Stored as an integer; values are a database contract. Append only — reordering
/// or removing one is a data break, not a rename.
/// </summary>
/// <remarks>
/// <b>Only <see cref="Poster"/> is ever written</b>, because AniList publishes one
/// cover per title and that is the whole of what it has. The rest exist because
/// D25's second schema warning is the reason this enum exists at all: poster,
/// banner, logo and backdrop are a set rather than a field, and a table keyed by
/// kind is what stops the arity-1 mistake D17 spent Phase 5a undoing for identity
/// being repeated for art.
///
/// They were to be filled from TVDB and TMDB in Phase 9b. D48 read those APIs'
/// terms and none of them is reachable from a self-hosted deployment — TMDB forbids
/// caching its content past six months, TheTVDB's free key bills every end user, and
/// fanart.tv wants a project key a public image cannot keep secret. The three unused
/// members stay anyway: this is stored as an integer, so removing them is a data
/// contract break in exchange for one arm of a switch.
/// </remarks>
public enum ImageKind
{
    Poster = 0,
    Banner = 1,
    ClearLogo = 2,
    Backdrop = 3
}

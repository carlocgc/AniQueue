namespace AniQueue.Core.Domain;

/// <summary>
/// What an image of a title actually shows.
///
/// Stored as an integer; values are a database contract. Append only — reordering
/// or removing one is a data break, not a rename.
/// </summary>
/// <remarks>
/// Only <see cref="Poster"/> is ever written, because AniList publishes one cover
/// per title. The others are reserved for a catalogue source that publishes more.
/// </remarks>
public enum ImageKind
{
    Poster = 0,
    Banner = 1,
    ClearLogo = 2,
    Backdrop = 3
}

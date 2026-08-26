namespace AniQueue.Core.Domain;

/// <summary>
/// Which size of a picture this row is.
///
/// Stored as an integer; values are a database contract. Append only — reordering
/// or removing one is a data break, not a rename.
/// </summary>
/// <remarks>
/// <b>Separate from <see cref="ImageKind"/> on purpose</b> (D48). That enum answers
/// "what does this picture show", and a size is not what it shows; folding sizes into
/// it would make every future kind×size pair a member of an append-only contract that
/// can never be tidied up again.
///
/// AniList publishes three sizes of the same cover and the two taken here are the two
/// that have somewhere to go: <see cref="Thumbnail"/> at 100px for a 40×60 list slot,
/// and <see cref="Full"/> at 460px for the detail dialog. The middle one buys nothing
/// — too soft for a hero image on a 2× display, nine times the bytes of what a list
/// row needs.
/// </remarks>
public enum ImageRendition
{
    /// <summary>AniList's <c>medium</c>, 100px wide and around 9.7 KB.</summary>
    Thumbnail = 0,

    /// <summary>
    /// AniList's <c>extraLarge</c>, 460px wide and around 83.3 KB.
    /// </summary>
    /// <remarks>
    /// D47 measured this size in order to <i>reject</i> it, because a fifty-row page
    /// carrying it costs 4.2 MB. That is a measurement about a list, and the dialog
    /// this exists for renders one image — so the number that disqualified it there
    /// is not an argument here (D48).
    /// </remarks>
    Full = 1
}

namespace AniQueue.Core.Domain;

/// <summary>
/// The form a title takes. <see cref="Unknown"/> is the default because imports
/// routinely omit it, and the brief requires missing metadata to degrade cleanly
/// rather than be invented.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum MediaType
{
    Unknown = 0,
    Tv = 1,
    Movie = 2,
    Ova = 3,
    Ona = 4,
    Special = 5,
    Music = 6
}

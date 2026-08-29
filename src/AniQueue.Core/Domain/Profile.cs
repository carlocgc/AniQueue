namespace AniQueue.Core.Domain;

/// <summary>
/// A user of the application. The MVP creates exactly one ("Default") and has no
/// authentication, but every piece of library data carries a ProfileId so that
/// multi-user support can be added later without a data migration.
/// </summary>
public class Profile
{
    /// <summary>
    /// Identifier of the single profile created at startup. Services that have no
    /// user context yet can resolve the default profile without a lookup.
    /// </summary>
    public const int DefaultProfileId = 1;

    public int Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// A short opaque name for this profile's library, minted once and never
    /// changed. A scoring reply carries it, so a reply built against a library that
    /// no longer exists is refused whole rather than matched row by row against a
    /// reused id space.
    /// </summary>
    /// <remarks>
    /// It lives on the profile rather than in configuration so that it dies with
    /// the database file it describes. Twelve hexadecimal characters, because its
    /// only job is to differ and a longer value gives whoever copies it between two
    /// documents more chances to get it wrong. Null only until the initializer
    /// fills it on the next start.
    /// </remarks>
    public string? LibraryKey { get; set; }

    /// <summary>
    /// Mints a value for <see cref="LibraryKey"/>. A truncated GUID, so it neither
    /// collides across two databases that never meet nor derives from a library's
    /// contents.
    /// </summary>
    public static string NewLibraryKey() => Guid.NewGuid().ToString("N")[..12];

    public ProfileSettings? Settings { get; set; }
}

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

    /// <summary>
    /// The optional login's password, hashed. Null means no password, which means
    /// nothing is locked — the state a fresh installation is in and stays in until
    /// somebody sets one.
    /// </summary>
    /// <remarks>
    /// Here rather than in <c>userconfig.json</c>, which is the one exception the
    /// one-home-per-setting rule has to make: a credential is not written to a file
    /// an operator opens in an editor, and is never read back to a page. It is on
    /// the profile rather than on its settings because it says who somebody is
    /// rather than how AniQueue looks to them.
    /// </remarks>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// What a sign-in cookie carries so it can be retired. Changed whenever the
    /// password is set, changed or cleared, which is what signs the other devices
    /// out.
    /// </summary>
    /// <remarks>
    /// Present whether or not a password is, so that nothing on a sign-in path has
    /// to write to mint one. Null only until the initializer fills it on the next
    /// start of a database that predates the column.
    /// </remarks>
    public string? SecurityStamp { get; set; }

    /// <summary>Mints a value for <see cref="SecurityStamp"/>.</summary>
    /// <remarks>
    /// A whole GUID rather than the truncated one <see cref="NewLibraryKey"/> uses.
    /// A library key is copied between two documents by hand and wants to be short;
    /// this is only ever compared, and its job is that a cookie issued before a
    /// password change cannot match one issued after.
    /// </remarks>
    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    public ProfileSettings? Settings { get; set; }
}

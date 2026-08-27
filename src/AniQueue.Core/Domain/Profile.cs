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
    /// A short opaque name for this profile's library, minted once and never changed
    /// (D50).
    /// </summary>
    /// <remarks>
    /// It exists so a scoring reply can say which library it was generated against.
    /// A reply is a document that leaves the application — pasted into a chat window,
    /// kept, pasted back — while every id inside it is a row key that means nothing
    /// outside the database file it came from. Delete the database, sync again, and
    /// the same id space comes back filled with different titles; a reply from before
    /// is then matched row by row against a library it never described, the ids that
    /// no longer exist are reported, and the ones that still do are applied to
    /// whatever now holds them. This is the field that lets the whole reply be
    /// refused instead.
    ///
    /// <b>On the profile rather than in <c>userconfig.json</c>, and the distinction is
    /// the point.</b> It has to be reborn exactly when the row space is, so it belongs
    /// to the database and dies with it. A key kept in configuration would survive the
    /// deletion it exists to detect. D20 separates operator configuration from user
    /// preference, and this is neither: it is a fact about a database file.
    ///
    /// Per profile rather than per database, because that is the other way one library
    /// becomes another — post-MVP, when §10's multi-user support lands and a reply
    /// built for one profile can be pasted into another.
    ///
    /// <b>Twelve hexadecimal characters, not a full GUID.</b> Its whole job is to
    /// differ, and 48 bits differ. What a longer one would cost is accuracy: whoever
    /// copies it between two documents — a person or a model — has more chances to get
    /// it wrong, and a transcription error here refuses a reply that was perfectly
    /// good. Null only on a row written before this column existed, until the
    /// initializer fills it on the next start.
    /// </remarks>
    public string? LibraryKey { get; set; }

    /// <summary>Mints a value for <see cref="LibraryKey"/>.</summary>
    /// <remarks>
    /// A truncated GUID rather than a counter or a hash of anything: it must not
    /// collide across two databases that never meet, and it must not be derivable
    /// from a library's contents — two people who imported the same MyAnimeList
    /// export would then hold the same key and neither reply would be refused.
    /// </remarks>
    public static string NewLibraryKey() => Guid.NewGuid().ToString("N")[..12];

    public ProfileSettings? Settings { get; set; }
}

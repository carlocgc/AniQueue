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

    public ProfileSettings? Settings { get; set; }
}

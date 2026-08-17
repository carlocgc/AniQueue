namespace AniQueue.Core.Domain;

/// <summary>
/// What happens when a source stops listing a title it previously listed (D19).
///
/// Applies only to titles carrying an identifier for that source. A row the source
/// has never listed is out of scope entirely, which is what protects a user
/// consolidating two separately-maintained lists — structurally, rather than by
/// their choosing the right setting.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncAbsencePolicy
{
    /// <summary>
    /// Surface it for review. The default, because it is safe for identical-list
    /// and consolidated-list users alike, so correctness does not depend on the
    /// user finding this setting.
    /// </summary>
    Flag = 0,

    /// <summary>
    /// Drop the library entry and its queue slot.
    /// </summary>
    /// <remarks>
    /// Not offered until Phase 8 supplies a backup and restore. A truncated
    /// response, a paging bug, a mistyped account or a profile turned private all
    /// look identical to "the user deleted everything", and an emptied library
    /// taking the hand-built queue with it is the one failure here with no recovery
    /// path in the product.
    /// </remarks>
    Remove = 1,

    /// <summary>Do nothing. The library only ever grows.</summary>
    Ignore = 2
}

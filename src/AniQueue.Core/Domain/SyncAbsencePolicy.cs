namespace AniQueue.Core.Domain;

/// <summary>
/// What happens when a source stops listing a title it previously listed.
///
/// Applies only to titles carrying an identifier for that source, so a row the
/// source has never listed is out of scope entirely — which is what protects a
/// user consolidating two separately-maintained lists.
///
/// Stored as an integer; values are a database contract. Append only.
/// </summary>
public enum SyncAbsencePolicy
{
    /// <summary>
    /// Surface it for review. The default, because it is safe for identical-list
    /// and consolidated-list users alike.
    /// </summary>
    Flag = 0,

    /// <summary>
    /// Drop the library entry and its queue slot. Not offered: a truncated
    /// response, a paging bug, a mistyped account or a profile turned private all
    /// look identical to a deliberate deletion, and an emptied library takes the
    /// hand-built queue with it.
    /// </summary>
    Remove = 1,

    /// <summary>Do nothing. The library only ever grows.</summary>
    Ignore = 2
}

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
    /// Hold each one until the user says. The default, because it is safe for
    /// identical-list and consolidated-list users alike.
    /// </summary>
    Flag = 0,

    /// <summary>
    /// Drop the library entry and its queue slot, keeping the catalogue row.
    /// Guarded: a fetch that returned nothing, or one dropping more than
    /// <see cref="AniQueue.Core.Sync.AbsenceRemovalCap"/> allows, holds instead.
    /// </summary>
    Remove = 1,

    /// <summary>
    /// Do nothing. The library only ever grows. Shown as <i>Keep them</i>; the name
    /// stays because <c>userconfig.json</c> stores this value by name.
    /// </summary>
    Ignore = 2
}

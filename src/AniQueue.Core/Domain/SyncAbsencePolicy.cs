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
    /// Not offered. It was gated on a backup and restore that D33 has since
    /// declined — the recovery path is the operator's own copy of the database file
    /// under <c>/data</c>, which is outside the application and outside any mistake
    /// it could make. A truncated response, a paging bug, a mistyped account or a
    /// profile turned private all look identical to "the user deleted everything",
    /// and an emptied library taking the hand-built queue with it is the one failure
    /// here that nothing in the product can undo.
    ///
    /// What it now waits for is the guards D19 lists: honour absence only when the
    /// fetch is structurally complete, never act on an empty or near-empty response,
    /// and cap how much one unattended run may remove before downgrading to
    /// <see cref="Flag"/>.
    /// </remarks>
    Remove = 1,

    /// <summary>Do nothing. The library only ever grows.</summary>
    Ignore = 2
}

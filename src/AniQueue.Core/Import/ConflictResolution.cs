namespace AniQueue.Core.Import;

/// <summary>
/// What to do with an entry the importer could not confidently identify.
///
/// The importer refuses to guess, but refusing to guess is not the same as
/// refusing to help: the user knows whether two titles are the same thing, so
/// the preview offers the decision rather than leaving the row permanently stuck.
/// </summary>
public enum ConflictResolution
{
    /// <summary>
    /// Leave the library untouched. The default, because doing nothing is always
    /// recoverable and the other two options are not.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Treat the incoming entry as the same title as the existing one: adopt the
    /// source identifier and metadata onto the existing record, and apply the
    /// watch progress to its library entry.
    ///
    /// Adopting the identifier is the point — it is what stops the same entry
    /// conflicting again on every future import.
    /// </summary>
    LinkToExisting = 1,

    /// <summary>
    /// Treat them as genuinely different titles and add the incoming one
    /// alongside the existing record.
    /// </summary>
    ImportAsNew = 2
}

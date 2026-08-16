namespace AniQueue.Core.Import;

/// <summary>What committing the import would do to one parsed entry.</summary>
public enum ImportAction
{
    /// <summary>The title is not in the library; it will be added.</summary>
    Create = 0,

    /// <summary>Matched an existing title, and at least one field differs.</summary>
    Update = 1,

    /// <summary>
    /// Matched, and nothing differs. This is what makes re-importing the same
    /// export a no-op rather than a duplicate.
    /// </summary>
    Unchanged = 2,

    /// <summary>
    /// Matched more than one existing title, or matched only by title with no
    /// source identifier to confirm it. Never applied automatically — an ambiguous
    /// merge is far harder to undo than a skipped row.
    /// </summary>
    Conflict = 3
}

using AniQueue.Core.Domain;

namespace AniQueue.Core.Import;

/// <summary>One parsed entry, paired with what committing would do to it.</summary>
public sealed record ImportPreviewItem
{
    public required ParsedLibraryEntry Entry { get; init; }

    public required ImportAction Action { get; init; }

    /// <summary>The matched title's id, when one was matched.</summary>
    public int? ExistingAnimeId { get; init; }

    /// <summary>
    /// Human-readable description of what would change, so the user can see the
    /// consequence rather than trusting a verb.
    /// </summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>Why an entry is a conflict, when it is one.</summary>
    public string? ConflictReason { get; init; }

    /// <summary>
    /// The user's decision for a conflicting entry. Settable rather than init-only
    /// because the preview is the object the UI binds to while the user works
    /// through the conflicts; only <see cref="ImportAction.Conflict"/> items
    /// consult it.
    /// </summary>
    public ConflictResolution Resolution { get; set; } = ConflictResolution.Skip;

    /// <summary>The title this entry was matched against, for display.</summary>
    public string? ExistingTitle { get; init; }
}

/// <summary>
/// The complete effect of an import, computed without writing anything.
///
/// The user sees this and confirms before the database is touched at all — the
/// brief is explicit that uploading must never mutate anything by itself.
/// </summary>
public sealed record ImportPreview
{
    public required string FormatName { get; init; }

    public required IReadOnlyList<ImportPreviewItem> Items { get; init; }

    public required IReadOnlyList<ImportProblem> Problems { get; init; }

    /// <summary>True when the file could not be read at all; nothing can be committed.</summary>
    public bool IsFileRejected { get; init; }

    public int CreateCount => Count(ImportAction.Create);

    public int UpdateCount => Count(ImportAction.Update);

    public int UnchangedCount => Count(ImportAction.Unchanged);

    public int ConflictCount => Count(ImportAction.Conflict);

    public int InvalidCount => Problems.Count(p => p.RecordNumber is not null);

    /// <summary>Conflicts the user has decided to act on rather than skip.</summary>
    public int ResolvedConflictCount =>
        Items.Count(i => i.Action == ImportAction.Conflict && i.Resolution != ConflictResolution.Skip);

    /// <summary>Whether committing would do anything at all.</summary>
    public bool HasApplicableChanges => CreateCount > 0 || UpdateCount > 0 || ResolvedConflictCount > 0;

    public int CompletedCount => CountStatus(LibraryStatus.Completed);

    public int WatchingCount => CountStatus(LibraryStatus.Watching);

    public int PlanningCount => CountStatus(LibraryStatus.Planning);

    public static ImportPreview Rejected(string formatName, IReadOnlyList<ImportProblem> problems) => new()
    {
        FormatName = formatName,
        Items = [],
        Problems = problems,
        IsFileRejected = true
    };

    private int Count(ImportAction action) => Items.Count(i => i.Action == action);

    // Counts every parsed entry, not only the ones that would change, so the
    // figures match what the user sees in their source application.
    private int CountStatus(LibraryStatus status) => Items.Count(i => i.Entry.Status == status);
}

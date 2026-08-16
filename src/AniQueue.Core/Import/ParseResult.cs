namespace AniQueue.Core.Import;

/// <summary>
/// What a parser produced: the entries it understood, and everything it could not.
/// </summary>
public sealed record ParseResult
{
    public required IReadOnlyList<ParsedLibraryEntry> Entries { get; init; }

    public required IReadOnlyList<ImportProblem> Problems { get; init; }

    /// <summary>
    /// True when the file itself could not be read at all — malformed XML, over
    /// the size limit — as opposed to individual records being unusable.
    /// </summary>
    public bool IsFileRejected { get; init; }

    public static ParseResult Rejected(string reason) => new()
    {
        Entries = [],
        Problems = [new ImportProblem(reason)],
        IsFileRejected = true
    };
}

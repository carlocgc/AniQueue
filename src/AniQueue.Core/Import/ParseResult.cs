using AniQueue.Core.Domain;

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

    /// <summary>
    /// Combines the results of parsing several payloads that together form one
    /// fetch (§5).
    /// </summary>
    /// <remarks>
    /// <b>One bad part rejects the whole.</b> A fetch that arrived in four responses
    /// and could only be read in three is not three-quarters of a library; it is a
    /// library with a quarter missing, and absence is exactly what a sync is
    /// entitled to act on. Reporting it as a partial success is how a chunking bug
    /// turns into a mass deletion (D19).
    ///
    /// Entries claiming an identifier an earlier part already claimed are dropped
    /// rather than concatenated. Within one payload a repeated identifier is a real
    /// contradiction and the preview surfaces it as a conflict; across payloads it
    /// is an artifact of how the list was chunked, and asking the user to resolve
    /// several hundred of those would be the pipeline blaming them for its own
    /// paging.
    /// </remarks>
    public static ParseResult Merge(IEnumerable<ParseResult> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var entries = new List<ParsedLibraryEntry>();
        var problems = new List<ImportProblem>();
        var claimed = new HashSet<ExternalIdentifier>();
        var rejected = false;

        foreach (var part in parts)
        {
            rejected |= part.IsFileRejected;
            problems.AddRange(part.Problems);

            foreach (var entry in part.Entries)
            {
                // Tested before anything is claimed, so a dropped entry leaves no
                // half-registered identifier behind to reject a later, legitimate one.
                if (entry.ExternalIds.Any(claimed.Contains))
                {
                    continue;
                }

                foreach (var identifier in entry.ExternalIds)
                {
                    claimed.Add(identifier);
                }

                entries.Add(entry);
            }
        }

        return rejected
            ? new ParseResult { Entries = [], Problems = problems, IsFileRejected = true }
            : new ParseResult { Entries = entries, Problems = problems };
    }
}

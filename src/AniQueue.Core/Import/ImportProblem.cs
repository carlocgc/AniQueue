namespace AniQueue.Core.Import;

/// <summary>
/// Something wrong with one record, or with the file as a whole.
///
/// A problem never aborts the import. A single malformed record in a
/// three-thousand entry export should cost the user that record, not the whole
/// file — so problems are collected, surfaced in the preview, and the rest
/// proceeds. <see cref="RecordNumber"/> is null for file-level problems.
/// </summary>
public sealed record ImportProblem(string Message, int? RecordNumber = null, string? Title = null)
{
    public override string ToString() =>
        RecordNumber is null ? Message : $"Record {RecordNumber}: {Message}";
}

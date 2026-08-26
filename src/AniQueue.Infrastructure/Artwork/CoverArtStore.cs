using AniQueue.Core.Artwork;
using AniQueue.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Artwork;

/// <summary>
/// The cached pictures on disk (D47).
/// </summary>
/// <remarks>
/// <b>Under <c>&lt;data&gt;/covers/</c>, derived from the database path.</b> §6
/// forbids image binaries in the database, and §9 already had to solve the non-root
/// bind-mount problem for the database file — solving it once solves it here. It also
/// means the sample profile gets its own cache without anything being configured,
/// because Phase 10a moved that profile into its own directory and the path this
/// reads is the one that moved.
///
/// <b>Disk is the authority on what is cached, not the table.</b> A row saying a
/// picture is cached is a claim; this is where it is checked. Somebody reclaiming
/// space by deleting the covers directory is the same instinct that makes deleting
/// <c>data/sample</c> safe, and it heals on the next tick rather than leaving every
/// row pointing at a file that is not there.
/// </remarks>
public sealed class CoverArtStore(IOptions<AniQueueDatabaseOptions> databaseOptions)
{
    private const string DirectoryName = "covers";

    private readonly string? _root = ResolveRoot(databaseOptions.Value);

    /// <summary>
    /// False when there is nowhere to cache to — an in-memory database in a test.
    /// </summary>
    public bool IsAvailable => _root is not null;

    /// <summary>Whether the bytes for this row are actually present.</summary>
    public bool Exists(int animeId, string? contentHash, string? fileExtension) =>
        PathFor(animeId, contentHash, fileExtension) is { } path && File.Exists(path);

    /// <summary>
    /// Writes a picture, atomically.
    /// </summary>
    /// <remarks>
    /// Through a temporary file and a rename, because the URL that will serve these
    /// bytes is immutable and cached for a year. A reader arriving mid-write would
    /// otherwise cache half an image forever, under an address that by construction
    /// will never be requested again.
    /// </remarks>
    public async Task WriteAsync(
        int animeId,
        string contentHash,
        string fileExtension,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (PathFor(animeId, contentHash, fileExtension) is not { } path)
        {
            return;
        }

        Directory.CreateDirectory(_root!);

        var staging = path + ".partial";
        await File.WriteAllBytesAsync(staging, content, cancellationToken);
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>Opens a cached picture for streaming, or null when it is not there.</summary>
    /// <remarks>
    /// Asynchronous and sequential because the caller is an HTTP endpoint streaming
    /// straight to a response, and the access pattern is one pass front to back.
    /// </remarks>
    public Stream? OpenRead(int animeId, string contentHash, string fileExtension)
    {
        if (PathFor(animeId, contentHash, fileExtension) is not { } path)
        {
            return null;
        }

        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Not exceptional. The row said it was here and it is not, which is the
            // case the job's next pass exists to repair; the caller answers 404 and
            // the page falls back to a colour block in the meantime.
            return null;
        }
    }

    /// <summary>
    /// Deletes every cached file nothing claims.
    /// </summary>
    /// <remarks>
    /// A row deleted by the cascade behind a removed title cannot reach the
    /// filesystem, and neither can a picture that has been replaced by a new one at a
    /// different hash. Both leave a file behind, and both are the same problem: a
    /// name on disk that no row expects. Listing the directory once and subtracting
    /// what is claimed handles them together, and needs no bookkeeping at the moment
    /// of deletion — which is the part that would otherwise have to be remembered in
    /// several places and would be forgotten in one of them.
    /// </remarks>
    public int RemoveUnclaimed(IReadOnlySet<string> claimedFileNames)
    {
        ArgumentNullException.ThrowIfNull(claimedFileNames);

        if (_root is null || !Directory.Exists(_root))
        {
            return 0;
        }

        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(_root))
        {
            var name = Path.GetFileName(path);
            if (claimedFileNames.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException)
            {
                // Something else has it open. It will still be unclaimed next time,
                // and a file that survives one sweep costs a few kilobytes.
            }
            catch (UnauthorizedAccessException)
            {
                // Same answer, and the likelier one under a bind mount owned by
                // another uid — which is §9's problem rather than this pass's, and
                // not worth failing a run over.
            }
        }

        return removed;
    }

    private string? PathFor(int animeId, string? contentHash, string? fileExtension) =>
        _root is not null && contentHash is { Length: > 0 } hash && fileExtension is { Length: > 0 } extension
            ? Path.Combine(_root, CoverImageResolver.CacheFileName(animeId, hash, extension))
            : null;

    private static string? ResolveRoot(AniQueueDatabaseOptions options)
    {
        if (options.IsInMemory)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Path));

        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, DirectoryName);
    }
}

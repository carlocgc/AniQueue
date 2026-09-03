namespace AniQueue.Core.Import;

/// <summary>
/// Buffers an input stream up to a hard ceiling, so a size cap is enforced before
/// any parsing begins.
/// </summary>
/// <remarks>
/// Shared by every parser rather than reimplemented per format. The bound is
/// applied while copying rather than read from a header or a
/// <see cref="Stream.Length"/>: an upload stream may not report one, and a remote
/// response's <c>Content-Length</c> is supplied by the other end and is not
/// evidence of anything.
/// </remarks>
internal static class LimitedBuffer
{
    /// <summary>
    /// Reads <paramref name="input"/> into memory, or returns null once
    /// <paramref name="maxBytes"/> would be exceeded. The caller owns the stream
    /// that comes back.
    /// </summary>
    public static async Task<MemoryStream?> ReadAsync(
        Stream input,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];

        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                await buffer.DisposeAsync();
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;
        return buffer;
    }
}

namespace AniQueue.Core.Library;

/// <summary>
/// What a delete is about to remove, in the three numbers somebody has to read
/// before pressing it.
/// </summary>
/// <param name="Titles">Titles in this profile's library.</param>
/// <param name="Queued">Of those, the ones holding a slot in Up Next.</param>
/// <param name="Pictures">Cached picture files on disk, across every title.</param>
public sealed record LibraryContents(int Titles, int Queued, int Pictures)
{
    public static LibraryContents Empty { get; } = new(0, 0, 0);

    public bool IsEmpty => Titles == 0 && Queued == 0 && Pictures == 0;
}

/// <summary>
/// Managing the data AniQueue has accumulated, as opposed to reading it.
///
/// Its own surface rather than methods on <see cref="ILibraryService"/>, which
/// answers questions and writes nothing.
/// </summary>
/// <remarks>
/// None of this is a backup or an undo. The database file is the backup, and the
/// copy is the operator's to keep.
/// </remarks>
public interface ILibraryMaintenance
{
    /// <summary>
    /// The counts a confirmation dialog names, so it can say what it is about to
    /// delete rather than asking somebody to type a phrase.
    /// </summary>
    Task<LibraryContents> GetContentsAsync(int profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every cached picture file, leaving the rows that describe them.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    /// <remarks>
    /// The rows are deliberately untouched. A row's cached file going missing is
    /// already a state the artwork pass repairs — it is how somebody reclaiming space
    /// by deleting the directory is handled — so the cache heals itself afterwards
    /// without anything having to re-derive what it once held.
    /// </remarks>
    Task<int> DeleteArtworkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties the library: every title, the queue, the scores, the pictures and the
    /// run history.
    /// </summary>
    /// <returns>What was there before, so the page can report what went.</returns>
    /// <remarks>
    /// <b>The profile row survives</b>, and that is what "leaving the settings in
    /// place" means: <c>ProfileSettings</c> hangs off it, so deleting the profile
    /// would reset the theme and the title language this promises to keep, and the
    /// initializer would mint a replacement on the next start.
    ///
    /// <b>The run history goes with the library it describes.</b> A run reading
    /// "changed 826" is a statement about rows that are gone.
    ///
    /// <b>Sync is left switched on.</b> This is about data; quietly changing a
    /// setting is a different act, and the dialog says what the next run will bring
    /// back instead.
    /// </remarks>
    Task<LibraryContents> DeleteEverythingAsync(int profileId, CancellationToken cancellationToken = default);
}

namespace AniQueue.Core.Domain;

/// <summary>
/// One picture of one title, from one source: where it can be fetched from,
/// whether it has been, and what to serve it as (D47).
///
/// This replaced <c>Anime.CoverImageUrl</c>. D25 predicted that column would not
/// survive a second image kind; it did not survive the first one either, because it
/// was written through the import merge, which preserves whatever is already stored
/// rather than overwriting it. Repointing the parser at a different size would have
/// given the right URL to titles arriving afterwards and left every existing row
/// holding the old one — an entire library quietly cached at nine times the size the
/// page needed, visible in no build and no test. Inserting a missing row here is not
/// the same operation as overwriting a scalar, so a sync fills existing titles in
/// without touching the field-preservation rules D18 and D21 depend on.
/// </summary>
/// <remarks>
/// <b>The bytes are not here.</b> §6 forbids image binaries in the database, so the
/// file lives under <c>&lt;data&gt;/art/{kind}/</c> and this row records where it came
/// from and what happened. Disk is the authority on whether it is actually cached:
/// the job's precondition asks the filesystem as well as this row, so deleting the
/// cache directory to reclaim space heals within a tick instead of breaking every
/// image permanently.
/// </remarks>
public class AnimeImage
{
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public ImageKind Kind { get; set; }

    /// <summary>Who published this picture. Not who published the title.</summary>
    /// <remarks>
    /// <see cref="AnimeSource.Manual"/> never appears: a hand-created title has no
    /// art to fetch. This was to become the column telling several rows of the same
    /// <see cref="Kind"/> apart once Phase 9b added TVDB and TMDB; D48 declined both,
    /// so <see cref="AnimeSource.AniList"/> is the only value that ever appears here.
    /// What actually gives a title more than one row of a kind is rendition — a
    /// thumbnail for a list slot and a full-size cover for the detail dialog.
    /// </remarks>
    public AnimeSource Source { get; set; }

    /// <summary>
    /// Where the picture is, as the source published it.
    /// </summary>
    /// <remarks>
    /// <b>Also the invalidation key.</b> AniList's URLs carry a content hash —
    /// <c>bx16498-buvcRTBx4NSm.jpg</c> — so replacing a title's art changes this
    /// value, and a change here clears both failure states and re-fetches. That is
    /// why nothing compares timestamps to decide whether art is stale: the source
    /// says so by changing its mind about the address.
    ///
    /// It is fetched rather than rendered, which is a stronger claim than the parser
    /// was making when it only checked the scheme — the host is checked against a
    /// constant list before any request is made (D47, §6).
    /// </remarks>
    public required string RemoteUrl { get; set; }

    /// <summary>
    /// The hash of the cached bytes, or null while nothing has been cached.
    /// </summary>
    /// <remarks>
    /// Doing double duty deliberately: it is what says a fetch succeeded, and it is
    /// what makes the served URL immutable. <c>/art/{kind}/{id}/{hash}</c> can be given
    /// a year's <c>max-age</c> because replaced art arrives at a different address, so
    /// a browser is never stale and never spends a request revalidating.
    /// </remarks>
    public string? ContentHash { get; set; }

    /// <summary>
    /// The URL the cached bytes actually came from, or null while none have.
    /// </summary>
    /// <remarks>
    /// <b>Two fields because there are two facts.</b> <see cref="RemoteUrl"/> is the
    /// picture that should be shown and this is the picture that is being shown, and
    /// collapsing them would mean choosing which failure to have: either replaced art
    /// is never noticed, or the row is blanked the moment the URL changes and the
    /// page shows a colour block for a title it was showing art for a second ago.
    /// Outstanding work is "these two disagree", which covers a title that has never
    /// been fetched and one whose art has been replaced with the same comparison.
    /// </remarks>
    public string? FetchedUrl { get; set; }

    /// <summary>
    /// The extension the cached file was written with, from its content type.
    /// </summary>
    /// <remarks>
    /// Taken from what the server said it sent rather than from the URL's path,
    /// because the path is the one part of this that a third party controls.
    /// </remarks>
    public string? FileExtension { get; set; }

    public long? ByteCount { get; set; }

    /// <summary>When the cached file arrived, or null while it has not.</summary>
    /// <remarks>
    /// Read and displayed, never compared in a <c>WHERE</c>, so it stays a
    /// <see cref="DateTimeOffset"/> under the rule Phase 6b paid to learn. Nothing
    /// ages art out on a clock — <see cref="RemoteUrl"/> changing is the only thing
    /// that re-opens a settled row.
    /// </remarks>
    public DateTimeOffset? FetchedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>
    /// True when the failure was about the picture rather than the network.
    /// </summary>
    /// <remarks>
    /// A 404, a body that is not an image, one over the size cap, or a host that is
    /// not on the allowlist are all facts about this URL that will not change while
    /// it stays this URL, so retrying is spending a request to be told the same
    /// thing. A timeout, a 5xx, a 429 or a dropped connection are facts about a
    /// moment, and get <see cref="AttemptCount"/> tries.
    /// </remarks>
    public bool FailureIsPermanent { get; set; }

    /// <summary>
    /// Transient failures so far for this URL. Reset when the URL changes.
    /// </summary>
    /// <remarks>
    /// The bound lives here rather than in the runner because D40 took rescheduling
    /// away from jobs, and something still has to stop a permanently unreachable
    /// title being asked about on every tick for the life of the installation.
    /// </remarks>
    public int AttemptCount { get; set; }
}

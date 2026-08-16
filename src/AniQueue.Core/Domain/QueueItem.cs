namespace AniQueue.Core.Domain;

/// <summary>
/// One slot in the manually ordered Up Next queue.
///
/// A slot holds *either* a single anime or an entire franchise, never both and
/// never neither — enforced in the database by a check constraint (D1). This is
/// why the queue is its own table: the brief put a QueuePosition column on
/// <see cref="LibraryEntry"/>, but a franchise has no LibraryEntry row, so that
/// design could not represent "the Slayers franchise is at position 7".
///
/// <see cref="Position"/> is a plain contiguous integer. There is deliberately no
/// unique index over (ProfileId, Position): SQLite checks uniqueness per
/// statement rather than at commit, so any reorder that shifts a block of rows
/// would collide mid-transaction and abort. Contiguity and uniqueness are instead
/// invariants of the queue service, applied inside one transaction — which makes
/// the reorder tests load-bearing rather than decorative (D2).
/// </summary>
public class QueueItem
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    /// <summary>Zero-based, contiguous within a profile. Lower is sooner.</summary>
    public int Position { get; set; }

    public int? AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public int? FranchiseId { get; set; }

    public Franchise? Franchise { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    /// True when this slot represents a whole franchise rather than one title.
    /// Mirrors the check constraint and keeps call sites from re-deriving it.
    /// </summary>
    public bool IsFranchise => FranchiseId is not null;
}

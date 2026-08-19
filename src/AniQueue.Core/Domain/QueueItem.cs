namespace AniQueue.Core.Domain;

/// <summary>
/// One slot in the manually ordered Up Next queue: a single title, in a position.
///
/// A slot is one title and never a group (D15). A group is not something you sit
/// down and watch, so it has no place in a list whose whole job is to say what to
/// watch next — and since D23 there are no groups here to hold anyway. What lines
/// up a run of seasons appends them individually, which is what makes it possible
/// to put something else between two of them.
///
/// <see cref="Position"/> is a plain contiguous integer. There is deliberately no
/// unique index over (ProfileId, Position): SQLite checks uniqueness per statement
/// rather than at commit, so any reorder that shifts a block of rows would collide
/// mid-transaction and abort. Contiguity and uniqueness are instead invariants of
/// the queue service, applied inside one transaction — which makes the reorder
/// tests load-bearing rather than decorative (D2).
///
/// The queue keeps its own table even though every slot now references exactly one
/// anime, and D15 records why: reordering should not write to wide library rows
/// that imports are contending for on a single-writer database.
/// </summary>
public class QueueItem
{
    public int Id { get; set; }

    public int ProfileId { get; set; }

    /// <summary>Zero-based, contiguous within a profile. Lower is sooner.</summary>
    public int Position { get; set; }

    public int AnimeId { get; set; }

    public Anime? Anime { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

namespace AniQueue.Core.Domain;

/// <summary>
/// One slot in the manually ordered Up Next queue: a single title, in a position.
///
/// A slot is never a franchise (D15). A franchise is a grouping of titles and an
/// action that queues them; it is not itself something you sit down and watch, so
/// it has no place in a list whose whole job is to say what to watch next. Queueing
/// a franchise appends its titles individually, in viewing order, which is what
/// makes it possible to put something else between two seasons.
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

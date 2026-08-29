namespace AniQueue.Core.Domain;

/// <summary>
/// One slot in the manually ordered Up Next queue: a single title, in a position.
/// Lining up a run of seasons appends them individually, so something else can be
/// put between two of them.
/// </summary>
/// <remarks>
/// <see cref="Position"/> is a plain contiguous integer with no unique index over
/// (ProfileId, Position): SQLite checks uniqueness per statement rather than at
/// commit, so any reorder that shifts a block of rows would collide
/// mid-transaction. Contiguity and uniqueness are invariants of the queue service
/// instead, applied inside one transaction — which is what makes the reorder tests
/// load-bearing.
/// </remarks>
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

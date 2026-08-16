namespace AniQueue.Core.Domain;

/// <summary>
/// A group of titles the user wants to treat as a single backlog decision — for
/// example every season, film, OVA and special of one series.
///
/// Franchises are created and curated by hand. There is no automatic detection in
/// the MVP, deliberately: guessing groupings wrongly is worse than not guessing.
///
/// Ordering *within* a franchise lives on <see cref="Anime.FranchiseOrder"/>
/// rather than here, because it is a property of each entry's place in the
/// viewing order.
/// </summary>
public class Franchise
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Where this franchise sits when franchises are listed manually.</summary>
    public int ManualSortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Member titles. Order by <see cref="Anime.FranchiseOrder"/>, not by Id.</summary>
    public ICollection<Anime> Entries { get; set; } = [];
}

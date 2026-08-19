using AniQueue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AniQueue.Infrastructure.Persistence.Configurations;

public class AnimeRelationConfiguration : IEntityTypeConfiguration<AnimeRelation>
{
    public void Configure(EntityTypeBuilder<AnimeRelation> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ExternalId).IsRequired().HasMaxLength(64);
        builder.Property(r => r.RelatedExternalId).IsRequired().HasMaxLength(64);

        // The whole row is the identity of an edge, so the whole row is the unique
        // key. It is what makes a re-run idempotent without reading first — though
        // the service reads anyway, because failing a batch of several hundred edges
        // over one duplicate would be a poor trade.
        builder
            .HasIndex(r => new { r.Source, r.ExternalId, r.RelationType, r.RelatedExternalId })
            .IsUnique();

        // The reverse lookup, and it is not optional. Edges are stored exactly as
        // fetched (D24), so half of any title's relations are rows where it is the
        // *related* end — a title whose own relations have never been fetched is
        // reachable only this way.
        builder.HasIndex(r => new { r.Source, r.RelatedExternalId });

        // No foreign key to Anime, deliberately, and this is the decision the table
        // exists to express. An edge routinely points at a title the user does not
        // own, and a foreign key would make storing it impossible — which would
        // discard exactly the edges that let an unowned middle season be discovered
        // later. Resolution happens through AnimeExternalId with a join, at read
        // time, where absence is an ordinary result rather than a constraint
        // violation.
    }
}

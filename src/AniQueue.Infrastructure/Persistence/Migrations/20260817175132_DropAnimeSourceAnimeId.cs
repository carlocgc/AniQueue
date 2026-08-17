using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropAnimeSourceAnimeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Safe only because AddAnimeExternalIds already copied every value into
            // AnimeExternalIds. The scaffolder warns about data loss and is right to;
            // the ordering of these two migrations is what makes it untrue here.
            migrationBuilder.DropIndex(
                name: "IX_Anime_Source_SourceAnimeId",
                table: "Anime");

            migrationBuilder.DropColumn(
                name: "SourceAnimeId",
                table: "Anime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceAnimeId",
                table: "Anime",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // Repopulate before the index is built, or a rollback would recreate an
            // empty column and the next import would treat the whole library as new.
            // Only the identifier matching the row's own provenance can come back —
            // a column holds one identity, which is the limitation D17 removed.
            migrationBuilder.Sql(
                """
                UPDATE Anime
                SET SourceAnimeId = (
                    SELECT ExternalId
                    FROM AnimeExternalIds
                    WHERE AnimeExternalIds.AnimeId = Anime.Id
                      AND AnimeExternalIds.Source = Anime.Source
                    LIMIT 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Anime_Source_SourceAnimeId",
                table: "Anime",
                columns: new[] { "Source", "SourceAnimeId" },
                unique: true,
                filter: "\"SourceAnimeId\" IS NOT NULL");
        }
    }
}

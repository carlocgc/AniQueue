using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimeExternalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnimeExternalIds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeExternalIds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnimeExternalIds_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeExternalIds_AnimeId_Source",
                table: "AnimeExternalIds",
                columns: new[] { "AnimeId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeExternalIds_Source_ExternalId",
                table: "AnimeExternalIds",
                columns: new[] { "Source", "ExternalId" },
                unique: true);

            // Carry every existing identifier across (D17). This is the whole point
            // of the migration: without it the new table starts empty and the next
            // import treats an entire library as new.
            //
            // Manual rows are excluded rather than copied. A hand-created title has
            // no issuing service, so a (Manual, <id>) row would assert an identity
            // that does not exist — and it is the null identifier on those rows that
            // forced the old index to be filtered in the first place.
            migrationBuilder.Sql(
                """
                INSERT INTO AnimeExternalIds (AnimeId, Source, ExternalId)
                SELECT Id, Source, TRIM(SourceAnimeId)
                FROM Anime
                WHERE SourceAnimeId IS NOT NULL
                  AND TRIM(SourceAnimeId) <> ''
                  AND Source <> 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeExternalIds");
        }
    }
}

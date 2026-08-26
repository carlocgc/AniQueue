using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AniListEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnimeImages_AnimeId_Kind_Source",
                table: "AnimeImages");

            migrationBuilder.AddColumn<int>(
                name: "Rendition",
                table: "AnimeImages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Genres",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Genres", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Studios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsAnimationStudio = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Studios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnimeGenres",
                columns: table => new
                {
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    GenreId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeGenres", x => new { x.AnimeId, x.GenreId });
                    table.ForeignKey(
                        name: "FK_AnimeGenres_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnimeGenres_Genres_GenreId",
                        column: x => x.GenreId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnimeStudios",
                columns: table => new
                {
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    StudioId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsMain = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeStudios", x => new { x.AnimeId, x.StudioId });
                    table.ForeignKey(
                        name: "FK_AnimeStudios_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnimeStudios_Studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "Studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeImages_AnimeId_Kind_Source_Rendition",
                table: "AnimeImages",
                columns: new[] { "AnimeId", "Kind", "Source", "Rendition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeGenres_GenreId",
                table: "AnimeGenres",
                column: "GenreId");

            migrationBuilder.CreateIndex(
                name: "IX_AnimeStudios_StudioId",
                table: "AnimeStudios",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_Genres_Name",
                table: "Genres",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Studios_Name",
                table: "Studios",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeGenres");

            migrationBuilder.DropTable(
                name: "AnimeStudios");

            migrationBuilder.DropTable(
                name: "Genres");

            migrationBuilder.DropTable(
                name: "Studios");

            migrationBuilder.DropIndex(
                name: "IX_AnimeImages_AnimeId_Kind_Source_Rendition",
                table: "AnimeImages");

            migrationBuilder.DropColumn(
                name: "Rendition",
                table: "AnimeImages");

            migrationBuilder.CreateIndex(
                name: "IX_AnimeImages_AnimeId_Kind_Source",
                table: "AnimeImages",
                columns: new[] { "AnimeId", "Kind", "Source" },
                unique: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimeRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RelationsFetchedAt",
                table: "AnimeExternalIds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverImageColor",
                table: "Anime",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "Anime",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnimeRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RelationType = table.Column<int>(type: "INTEGER", nullable: false),
                    RelatedExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeRelations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeRelations_Source_ExternalId_RelationType_RelatedExternalId",
                table: "AnimeRelations",
                columns: new[] { "Source", "ExternalId", "RelationType", "RelatedExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeRelations_Source_RelatedExternalId",
                table: "AnimeRelations",
                columns: new[] { "Source", "RelatedExternalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeRelations");

            migrationBuilder.DropColumn(
                name: "RelationsFetchedAt",
                table: "AnimeExternalIds");

            migrationBuilder.DropColumn(
                name: "CoverImageColor",
                table: "Anime");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "Anime");
        }
    }
}

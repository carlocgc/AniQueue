using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireFranchises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Curated franchises are dropped rather than converted, and there is
            // nothing to convert them into: D23 removes grouping as something the
            // application stores at all, so any equivalent has to be derived from
            // AniList's relation data rather than carried across.
            //
            // The queue is unaffected. Since D15 a slot references a title directly,
            // so nothing in it depends on the columns going here — which is what
            // makes this a straight drop rather than the data migration
            // FranchisesAreNotQueueItems had to be.
            //
            // ShowOptionalFranchiseEntries goes with them. It was a preference about
            // OptionalWithinFranchise, and a setting whose subject no longer exists
            // can only mislead.
            migrationBuilder.DropForeignKey(
                name: "FK_Anime_Franchises_FranchiseId",
                table: "Anime");

            migrationBuilder.DropTable(
                name: "Franchises");

            migrationBuilder.DropIndex(
                name: "IX_Anime_FranchiseId",
                table: "Anime");

            migrationBuilder.DropColumn(
                name: "ShowOptionalFranchiseEntries",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "FranchiseId",
                table: "Anime");

            migrationBuilder.DropColumn(
                name: "FranchiseOrder",
                table: "Anime");

            migrationBuilder.DropColumn(
                name: "OptionalWithinFranchise",
                table: "Anime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowOptionalFranchiseEntries",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FranchiseId",
                table: "Anime",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FranchiseOrder",
                table: "Anime",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OptionalWithinFranchise",
                table: "Anime",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Franchises",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ManualSortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Franchises", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Anime_FranchiseId",
                table: "Anime",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_ManualSortOrder",
                table: "Franchises",
                column: "ManualSortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_Name",
                table: "Franchises",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_Anime_Franchises_FranchiseId",
                table: "Anime",
                column: "FranchiseId",
                principalTable: "Franchises",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecommendationsRankTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Franchise placements are deleted rather than converted. EF's scaffolded
            // AlterColumn below would otherwise default their null AnimeId to 0,
            // leaving rows that point at a title which does not exist.
            //
            // Deletion is the honest treatment here, and it differs from D15's
            // migration deliberately. There a franchise queue slot stood for a real
            // intention — watch this group next — so it was expanded into the titles
            // that carried it. A franchise *ranking* stood for nothing that could
            // ever be acted on: applying a run caches onto LibraryEntry, a franchise
            // has none, so these rows were skipped by the apply path from the day
            // they were introduced. Nothing observable is lost by removing them.
            //
            // In practice this deletes nothing — Phase 9 is unbuilt and no code path
            // has ever created such a row — but a migration has to be correct for the
            // data the schema permitted, not the data we expect.
            migrationBuilder.Sql(
                """DELETE FROM "RecommendationRunItems" WHERE "AnimeId" IS NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_RecommendationRunItems_Franchises_FranchiseId",
                table: "RecommendationRunItems");

            migrationBuilder.DropIndex(
                name: "IX_RecommendationRunItems_FranchiseId",
                table: "RecommendationRunItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecommendationRunItems_AnimeXorFranchise",
                table: "RecommendationRunItems");

            migrationBuilder.DropColumn(
                name: "FranchiseId",
                table: "RecommendationRunItems");

            migrationBuilder.AlterColumn<int>(
                name: "AnimeId",
                table: "RecommendationRunItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Restores the schema, not the deleted rows. They were inert, and a run's
        /// candidate set is reconstructable from the titles it does contain (D4), so
        /// there is nothing to rebuild them from and nothing that would read them.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "AnimeId",
                table: "RecommendationRunItems",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "FranchiseId",
                table: "RecommendationRunItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_FranchiseId",
                table: "RecommendationRunItems",
                column: "FranchiseId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecommendationRunItems_AnimeXorFranchise",
                table: "RecommendationRunItems",
                sql: "(\"AnimeId\" IS NULL) <> (\"FranchiseId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_RecommendationRunItems_Franchises_FranchiseId",
                table: "RecommendationRunItems",
                column: "FranchiseId",
                principalTable: "Franchises",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Carries out D43: the rank leaves the database as it left the interchange.
    /// </summary>
    /// <remarks>
    /// SQLite cannot drop a column from a table carrying a check constraint, so EF
    /// rebuilds RecommendationRunItems here rather than issuing an ALTER. That is
    /// ordinary, and it is the reason to run this against a copy of a real database
    /// rather than only against a fresh one. Existing rows keep their scores,
    /// confidences and reasons — only the placement goes.
    ///
    /// The composite (RunId, Rank) index is replaced by a plain one on RunId. It was
    /// always the foreign key's index with a sort key appended, and the sort key is
    /// what went.
    /// </remarks>
    public partial class ScoringWithoutARank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecommendationRunItems_RunId_Rank",
                table: "RecommendationRunItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecommendationRunItems_RankPositive",
                table: "RecommendationRunItems");

            migrationBuilder.DropColumn(
                name: "Rank",
                table: "RecommendationRunItems");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_RunId",
                table: "RecommendationRunItems",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecommendationRunItems_RunId",
                table: "RecommendationRunItems");

            // Scaffolded as 0, corrected to 1. Down re-adds the CK_..._RankPositive
            // constraint below, and 0 violates it — so on a database with any run
            // items in it the generated Down could not run at all. 1 is the lowest
            // value the constraint permits and is as meaningless as 0 for a placement
            // nothing recorded, which is the honest state to restore to.
            migrationBuilder.AddColumn<int>(
                name: "Rank",
                table: "RecommendationRunItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_RunId_Rank",
                table: "RecommendationRunItems",
                columns: new[] { "RunId", "Rank" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecommendationRunItems_RankPositive",
                table: "RecommendationRunItems",
                sql: "\"Rank\" >= 1");
        }
    }
}

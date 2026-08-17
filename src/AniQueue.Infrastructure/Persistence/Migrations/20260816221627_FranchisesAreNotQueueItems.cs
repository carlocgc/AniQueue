using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FranchisesAreNotQueueItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing franchise slots are expanded before the column goes, not
            // dropped. EF's scaffolded AlterColumn would otherwise default their
            // null AnimeId to 0 — a slot pointing at a title that does not exist.
            //
            // Each queued franchise becomes its own titles, on the same terms the
            // application now uses: still Planning, not optional, not already
            // queued, in viewing order (D15).
            //
            // Positions are assigned from a high offset rather than packed onto the
            // end. Computing "max position + n" while inserting into the same table
            // is not reliably ordered in SQLite, and it does not need to be: the
            // relative order is what matters for display, and QueueService rewrites
            // positions to 0..n-1 on the next edit of any kind. A queue that arrives
            // non-contiguous repairing itself is a property the service already has
            // and is tested for.
            migrationBuilder.Sql(
                """
                INSERT INTO "QueueItems" ("ProfileId", "Position", "AnimeId", "AddedAt")
                SELECT
                    q."ProfileId",
                    1000000 + ROW_NUMBER() OVER (
                        PARTITION BY q."ProfileId"
                        ORDER BY q."Position", a."FranchiseOrder" IS NULL, a."FranchiseOrder", a."Title"),
                    a."Id",
                    q."AddedAt"
                FROM "QueueItems" q
                JOIN "Anime" a ON a."FranchiseId" = q."FranchiseId"
                JOIN "LibraryEntries" e ON e."AnimeId" = a."Id" AND e."ProfileId" = q."ProfileId"
                WHERE q."FranchiseId" IS NOT NULL
                  AND a."OptionalWithinFranchise" = 0
                  AND e."Status" = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM "QueueItems" y
                      WHERE y."ProfileId" = q."ProfileId" AND y."AnimeId" = a."Id");
                """);

            migrationBuilder.Sql("""DELETE FROM "QueueItems" WHERE "FranchiseId" IS NOT NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_QueueItems_Franchises_FranchiseId",
                table: "QueueItems");

            migrationBuilder.DropIndex(
                name: "IX_QueueItems_FranchiseId",
                table: "QueueItems");

            migrationBuilder.DropIndex(
                name: "IX_QueueItems_ProfileId_AnimeId",
                table: "QueueItems");

            migrationBuilder.DropIndex(
                name: "IX_QueueItems_ProfileId_FranchiseId",
                table: "QueueItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QueueItems_AnimeXorFranchise",
                table: "QueueItems");

            migrationBuilder.DropColumn(
                name: "FranchiseId",
                table: "QueueItems");

            migrationBuilder.AlterColumn<int>(
                name: "AnimeId",
                table: "QueueItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_AnimeId",
                table: "QueueItems",
                columns: new[] { "ProfileId", "AnimeId" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Restores the schema, not the data. Which slots were once a franchise is
        /// not recorded anywhere after Up runs, so a title expanded out of one stays
        /// an ordinary queued title on the way back. That is the honest outcome:
        /// inventing a regrouping would be a guess, and the expanded rows are a
        /// strictly more detailed statement of the same intent.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueueItems_ProfileId_AnimeId",
                table: "QueueItems");

            migrationBuilder.AlterColumn<int>(
                name: "AnimeId",
                table: "QueueItems",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "FranchiseId",
                table: "QueueItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_FranchiseId",
                table: "QueueItems",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_AnimeId",
                table: "QueueItems",
                columns: new[] { "ProfileId", "AnimeId" },
                unique: true,
                filter: "\"AnimeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_FranchiseId",
                table: "QueueItems",
                columns: new[] { "ProfileId", "FranchiseId" },
                unique: true,
                filter: "\"FranchiseId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QueueItems_AnimeXorFranchise",
                table: "QueueItems",
                sql: "(\"AnimeId\" IS NULL) <> (\"FranchiseId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_QueueItems_Franchises_FranchiseId",
                table: "QueueItems",
                column: "FranchiseId",
                principalTable: "Franchises",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

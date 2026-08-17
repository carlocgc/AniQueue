using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcePrecedence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastWrittenBySource",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceSyncSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    PrecedenceRank = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApplyUnattended = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConflictPolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    AbsencePolicy = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSyncSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceSyncSettings_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSyncSettings_ProfileId_Source",
                table: "SourceSyncSettings",
                columns: new[] { "ProfileId", "Source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceSyncSettings");

            migrationBuilder.DropColumn(
                name: "LastWrittenBySource",
                table: "LibraryEntries");
        }
    }
}

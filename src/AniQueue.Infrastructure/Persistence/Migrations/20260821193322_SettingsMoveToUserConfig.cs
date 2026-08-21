using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettingsMoveToUserConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludePersonalNotesInAiExport",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "RecommendationCandidateLimit",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "RecommendationHistorySize",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "RecommendationReturnTop",
                table: "ProfileSettings");

            migrationBuilder.AddColumn<long>(
                name: "DurationMilliseconds",
                table: "RecommendationRuns",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMilliseconds",
                table: "RecommendationRuns");

            migrationBuilder.AddColumn<bool>(
                name: "IncludePersonalNotesInAiExport",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecommendationCandidateLimit",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecommendationHistorySize",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecommendationReturnTop",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: true);
        }
    }
}

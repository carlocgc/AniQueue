using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropUnreadPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateFormat",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "DefaultQueueSize",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "DefaultRecommendationMode",
                table: "ProfileSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DateFormat",
                table: "ProfileSettings",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DefaultQueueSize",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultRecommendationMode",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}

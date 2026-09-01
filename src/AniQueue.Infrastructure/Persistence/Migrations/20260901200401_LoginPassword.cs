using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoginPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Profiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Profiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Profiles");
        }
    }
}

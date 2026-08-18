using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnattendedSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AbsentFlagged",
                table: "SyncRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChangesHeld",
                table: "SyncRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Schedule",
                table: "SourceSyncSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingFromSourceAt",
                table: "AnimeExternalIds",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AbsentFlagged",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "ChangesHeld",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "SourceSyncSettings");

            migrationBuilder.DropColumn(
                name: "MissingFromSourceAt",
                table: "AnimeExternalIds");
        }
    }
}

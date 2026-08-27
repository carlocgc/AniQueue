using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops <c>IsHidden</c> and its index (Phase 18b).
    /// </summary>
    /// <remarks>
    /// <b>Anything currently hidden comes back.</b> It returns to the backlog and to
    /// the scoring candidate set on this migration, which is the correct outcome
    /// under D11 — list membership lives outside AniQueue, so the honest way to say
    /// "stop offering me this" is to take the title off the AniList or MyAnimeList
    /// list it came from and let the next sync agree. It is still a visible change to
    /// somebody's library, which is why it is in the release notes and not only here.
    ///
    /// <b>Down restores the column and not the data.</b> Which entries were hidden is
    /// recorded nowhere else, so reversing this gives every row a false flag. There is
    /// nowhere to keep it: the point of the deletion is that the fact was only ever
    /// local.
    /// </remarks>
    public partial class HiddenIsDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LibraryEntries_ProfileId_IsHidden",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "LibraryEntries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_ProfileId_IsHidden",
                table: "LibraryEntries",
                columns: new[] { "ProfileId", "IsHidden" });
        }
    }
}
